using System.Globalization;
using System.IO.Compression;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Database;
using Dib4t.Infrastructure.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// <c>.dibbak</c> restore into the new app's database (legacy <c>restoreBackup</c> in
/// <c>repositories/databaseRepository.js</c>, PLAN.md §7.6). Two passes over the uploaded file, so a 1 GB backup is
/// never held in memory: (1) gunzip + validate everything, (2) drop every user collection and recreate each one from
/// the file: <c>create</c> with its options, ordered <c>insertMany</c> of the raw BSON documents in batches of 1000
/// (no re-serialization, validation bypassed), then its indexes without <c>_id_</c>, <c>v</c> and <c>ns</c>.
/// </summary>
/// <remarks>
/// Options and index specs are cleaned for MongoDB 9.0 first (<see cref="MongoCompatibility"/>); what the server still
/// refuses is left out and reported, never the documents. <see cref="VerifyAsync"/> is the built-in verification.
/// Also the backup (legacy <c>writeBackup</c>) and the statistics (legacy <c>getInfo</c>).
/// </remarks>
internal sealed class MongoDatabaseBackupService(IMongoDatabase database, IOptions<MongoOptions> options) : IDatabaseBackupService
{
    public const int InsertBatchSize = 1000;

    /// <summary>
    /// Legacy <c>a.name.localeCompare(b.name)</c> (ICU root collation, as the invariant culture compares with ICU):
    /// the order of the info table and of the collections in a backup.
    /// </summary>
    internal static readonly StringComparer CollectionOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None);

    public async Task<Result<BackupContents>> ValidateAsync(Stream backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var collector = new ContentsCollector();
        try
        {
            var summary = await CheckAsync(backup, collector, cancellationToken);
            return new BackupContents(
                new RestoreSummary(summary.CreatedAt, summary.Database, summary.Collections, summary.Documents),
                collector.Collections(), collector.Adjustments);
        }
        catch (BackupFormatException ex)
        {
            return Error.Validation(ex.Message);
        }
    }

    public async Task<IReadOnlyList<RestoreAdjustment>> ReplaceAsync(Stream backup, IRestoreProgress progress)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(progress);

        // The database is being replaced: nothing may cancel it half-way (a closed browser included).
        var restoring = CancellationToken.None;
        await DropUserCollectionsAsync(restoring);

        backup.Position = 0;
        await using var content = new VerifiedGzipReadStream(backup);
        var writer = new RestoreWriter(database, progress);
        await DibbakReader.ReadAsync(content, writer, validateDocuments: false, restoring);
        await writer.CompleteAsync(restoring);
        return writer.Refused;
    }

    /// <summary>Validate, then replace (the restore without the pre-restore backup and the verification; tests).</summary>
    internal async Task<Result<RestoreSummary>> RestoreAsync(Stream backup, CancellationToken cancellationToken)
    {
        var contents = await ValidateAsync(backup, cancellationToken);
        if (!contents.IsSuccess)
        {
            return contents.Error;
        }

        await ReplaceAsync(backup, NoProgress.Instance);
        return contents.Value.Summary;
    }

    public async Task<RestoreVerification> VerifyAsync(BackupContents contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var present = (await ListUserCollectionsAsync(cancellationToken)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var collections = new List<CollectionVerification>(contents.Collections.Count);
        foreach (var expected in contents.Collections)
        {
            if (!present.Contains(expected.Name))
            {
                collections.Add(new CollectionVerification(expected.Name, false, expected.Documents, 0, false, [.. expected.Indexes.Select(i => i.Name)]));
                continue;
            }

            var collection = database.GetCollection<RawBsonDocument>(expected.Name);
            var checksum = new DocumentChecksum();
            using (var cursor = await collection.FindAsync(FilterDefinition<RawBsonDocument>.Empty,
                new FindOptions<RawBsonDocument> { BatchSize = InsertBatchSize }, cancellationToken))
            {
                while (await cursor.MoveNextAsync(cancellationToken))
                {
                    foreach (var document in cursor.Current)
                    {
                        using (document)
                        {
                            checksum.Add(document.Slice);
                        }
                    }
                }
            }

            using var indexCursor = await collection.Indexes.ListAsync(cancellationToken);
            var indexes = (await indexCursor.ToListAsync(cancellationToken))
                .Select(i => new ExpectedIndex(i.GetValue("name", "").ToString()!, IndexKeyText(i.GetValue("key", new BsonDocument()))))
                .ToHashSet();
            collections.Add(new CollectionVerification(expected.Name, true, expected.Documents, checksum.Count,
                checksum.Count == expected.Documents && checksum.Value == expected.Checksum,
                [.. expected.Indexes.Where(i => !indexes.Contains(i)).Select(i => i.Name)]));
        }

        var inFile = contents.Collections.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return new RestoreVerification(collections, [.. present.Where(n => !inFile.Contains(n)).Order(CollectionOrder)]);
    }

    /// <summary>An index key as canonical Extended JSON (the same text for the file's spec and the server's).</summary>
    internal static string IndexKeyText(BsonValue key) =>
        key.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson });

    /// <summary>First pass: gunzip and check the whole file; nothing is written.</summary>
    /// <exception cref="BackupFormatException">The file is not a valid backup (legacy message).</exception>
    internal static async Task<DibbakSummary> CheckAsync(Stream backup, CancellationToken cancellationToken) =>
        await CheckAsync(backup, visitor: null, cancellationToken);

    private static async Task<DibbakSummary> CheckAsync(Stream backup, IDibbakVisitor? visitor, CancellationToken cancellationToken)
    {
        backup.Position = 0;
        await using var content = new VerifiedGzipReadStream(backup);
        try
        {
            return await DibbakReader.ReadAsync(content, visitor, validateDocuments: true, cancellationToken);
        }
        catch (BackupFormatException)
        {
            // Legacy gunzips the whole file before it parses it: a damaged gzip stream wins over a format error.
            await content.CopyToAsync(Stream.Null, cancellationToken);
            throw;
        }
    }

    private async Task DropUserCollectionsAsync(CancellationToken cancellationToken)
    {
        foreach (var collection in await ListUserCollectionsAsync(cancellationToken))
        {
            await database.DropCollectionAsync(collection.Name, cancellationToken);
        }
    }

    /// <summary>
    /// Legacy <c>listUserCollections</c>: collections (not views, not time series) whose name does not start with
    /// <c>system.</c>, in <see cref="CollectionOrder"/>, each with its <c>listCollections</c> options.
    /// </summary>
    private async Task<List<(string Name, BsonDocument Options)>> ListUserCollectionsAsync(CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        var collections = await cursor.ToListAsync(cancellationToken);
        return [.. collections
            .Where(c => c.GetValue("type", BsonNull.Value) is var type && (type.IsBsonNull || type == "collection")
                        && !c["name"].AsString.StartsWith("system.", StringComparison.Ordinal))
            .Select(c => (c["name"].AsString, c.GetValue("options", BsonNull.Value) is BsonDocument o ? o : []))
            .OrderBy(c => c.Item1, CollectionOrder)];
    }

    public async Task<DatabaseInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var stats = await database.RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1), cancellationToken: cancellationToken);
        var version = await ServerVersionAsync(cancellationToken);
        var collections = await ListUserCollectionsAsync(cancellationToken);
        var collectionsInfo = new List<CollectionInfo>(collections.Count);
        foreach (var (name, _) in collections)
        {
            collectionsInfo.Add(await CollectionStatsAsync(name, cancellationToken));
        }

        var server = MongoUrl.Create(options.Value.ConnectionString).Servers.First(); // Mongoose's connection.host/port: the first host
        return new DatabaseInfo(
            database.DatabaseNamespace.DatabaseName, server.Host, server.Port, version, collectionsInfo.Count,
            Number(stats, "objects"), Number(stats, "avgObjSize"), Number(stats, "dataSize"), Number(stats, "storageSize"),
            Number(stats, "indexes"), Number(stats, "indexSize"), collectionsInfo);
    }

    /// <summary>Legacy <c>getCollectionStats</c>: <c>collStats</c>, else the estimated count and the number of indexes.</summary>
    private async Task<CollectionInfo> CollectionStatsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var s = await database.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", name), cancellationToken: cancellationToken);
            return new CollectionInfo(name, Number(s, "count"), Number(s, "size"), Number(s, "avgObjSize") ?? 0,
                Number(s, "storageSize"), Number(s, "nindexes"), Number(s, "totalIndexSize"));
        }
        catch (MongoCommandException)
        {
            var collection = database.GetCollection<BsonDocument>(name);
            var count = await collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
            using var indexes = await collection.Indexes.ListAsync(cancellationToken);
            return new CollectionInfo(name, count, null, null, null, (await indexes.ToListAsync(cancellationToken)).Count, null);
        }
    }

    /// <summary>A numeric field as a JavaScript number; <see langword="null"/> (left out of the answer) when it is missing or not a number.</summary>
    private static double? Number(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToDouble() : null;

    private async Task<string?> ServerVersionAsync(CancellationToken cancellationToken)
    {
        var buildInfo = await database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: cancellationToken);
        return buildInfo.TryGetValue("version", out var version) && version.IsString ? version.AsString : null;
    }

    public async Task<BackupSummary> WriteBackupAsync(Stream output, string? createdBy, DateTime createdAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var version = await ServerVersionAsync(cancellationToken);
        var collections = await ListUserCollectionsAsync(cancellationToken);

        var response = new CuttableStream(output);
        var gzip = new GZipStream(response, CompressionLevel.Optimal, leaveOpen: true);
        try
        {
            var writer = new DibbakWriter(gzip);
            await writer.WriteMagicAsync(cancellationToken);
            await writer.WriteJsonFrameAsync(DibbakFormat.HeaderFrame, json =>
            {
                json.WriteStartObject();
                json.WriteString("format", DibbakFormat.FormatName);
                json.WriteNumber("version", DibbakFormat.FormatVersion);
                json.WriteString("createdAt", JsDate.ToIsoString(new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds()));
                json.WriteString("createdBy", createdBy); // null when unknown (legacy `createdBy || null`)
                json.WriteString("database", database.DatabaseNamespace.DatabaseName);
                if (version is not null)
                {
                    json.WriteString("serverVersion", version);
                }

                json.WriteStartArray("collections");
                collections.ForEach(c => json.WriteStringValue(c.Name));
                json.WriteEndArray();
                json.WriteEndObject();
            }, cancellationToken);

            long documents = 0;
            foreach (var (name, collectionOptions) in collections)
            {
                var collection = database.GetCollection<RawBsonDocument>(name);
                using var indexCursor = await collection.Indexes.ListAsync(cancellationToken);
                var indexes = await indexCursor.ToListAsync(cancellationToken);
                var count = await collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
                await writer.WriteJsonFrameAsync(DibbakFormat.CollectionFrame, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("name", name);
                    json.WritePropertyName("options");
                    JsJson.Write(json, collectionOptions);
                    json.WritePropertyName("indexes");
                    JsJson.Write(json, new BsonArray(indexes));
                    json.WritePropertyName("count");
                    JsJson.WriteNumber(json, count);
                    json.WriteEndObject();
                }, cancellationToken);

                using var cursor = await collection.FindAsync(FilterDefinition<RawBsonDocument>.Empty,
                    new FindOptions<RawBsonDocument> { BatchSize = InsertBatchSize }, cancellationToken);
                while (await cursor.MoveNextAsync(cancellationToken))
                {
                    foreach (var document in cursor.Current)
                    {
                        using (document)
                        {
                            await writer.WriteDocumentAsync(document, cancellationToken);
                        }

                        documents++;
                    }
                }
            }

            await writer.WriteJsonFrameAsync(DibbakFormat.EndFrame, json =>
            {
                json.WriteStartObject();
                json.WriteNumber("collections", collections.Count);
                json.WriteNumber("documents", documents);
                json.WriteEndObject();
            }, cancellationToken);

            await gzip.DisposeAsync(); // the final block and the gzip trailer
            await response.FlushAsync(cancellationToken);
            return new BackupSummary(collections.Count, documents);
        }
        catch
        {
            response.Cut();
            await gzip.DisposeAsync();
            throw;
        }
    }

    /// <summary>First pass: what the file holds, per collection (count, checksum, indexes) and the 9.0 adjustments.</summary>
    private sealed class ContentsCollector : IDibbakVisitor
    {
        private readonly List<(DibbakCollection Collection, DocumentChecksum Checksum)> _collections = [];

        public List<RestoreAdjustment> Adjustments { get; } = [];

        public ValueTask OnCollectionAsync(DibbakCollection collection, CancellationToken cancellationToken)
        {
            _collections.Add((collection, new DocumentChecksum()));
            Adjustments.AddRange(collection.Adjustments);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDocumentAsync(byte[] document, CancellationToken cancellationToken)
        {
            _collections[^1].Checksum.Add(document);
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<BackupCollectionContents> Collections() =>
        [
            .. _collections.Select(c => new BackupCollectionContents(
                c.Collection.Name, c.Checksum.Count, c.Checksum.Value,
                [.. c.Collection.IndexSpecs.Select(i => new ExpectedIndex(i.GetValue("name", "").ToString()!, IndexKeyText(i.GetValue("key", new BsonDocument()))))])),
        ];
    }

    private sealed class NoProgress : IRestoreProgress
    {
        public static readonly NoProgress Instance = new();

        public void Replacing(string collection, int collectionNumber, long documents)
        {
        }
    }

    private sealed class RestoreWriter(IMongoDatabase database, IRestoreProgress progress) : IDibbakVisitor
    {
        private static readonly InsertManyOptions InsertOptions = new() { IsOrdered = true, BypassDocumentValidation = true };

        private readonly List<RawBsonDocument> _batch = new(InsertBatchSize);
        private DibbakCollection? _current;
        private IMongoCollection<RawBsonDocument>? _target;
        private int _collections;
        private long _documents;

        /// <summary>Options and indexes the server refused although the cleanup kept them (left out, with its message).</summary>
        public List<RestoreAdjustment> Refused { get; } = [];

        public async ValueTask OnCollectionAsync(DibbakCollection collection, CancellationToken cancellationToken)
        {
            await CompleteAsync(cancellationToken);

            var create = new BsonDocument("create", collection.Name);
            foreach (var option in collection.CreateOptions)
            {
                create[option.Name] = option.Value; // like the legacy `cmd[n] = options[n]`
            }

            try
            {
                await database.RunCommandAsync<BsonDocument>(create, cancellationToken: cancellationToken);
            }
            catch (MongoCommandException ex) when (collection.CreateOptions.ElementCount > 0)
            {
                // The documents matter more than the options: create the collection without them.
                Refused.Add(new RestoreAdjustment(collection.Name, null, [.. collection.CreateOptions.Names], ServerMessage: ex.ErrorMessage));
                await database.RunCommandAsync<BsonDocument>(new BsonDocument("create", collection.Name), cancellationToken: cancellationToken);
            }

            _current = collection;
            _target = database.GetCollection<RawBsonDocument>(collection.Name);
            progress.Replacing(collection.Name, ++_collections, _documents);
        }

        public async ValueTask OnDocumentAsync(byte[] document, CancellationToken cancellationToken)
        {
            _batch.Add(new RawBsonDocument(document));
            if (_batch.Count == InsertBatchSize)
            {
                await FlushAsync(cancellationToken);
            }
        }

        /// <summary>Inserts what is left of the current collection, then creates its indexes.</summary>
        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_current is null)
            {
                return;
            }

            await FlushAsync(cancellationToken);
            if (_current.IndexSpecs.Count > 0)
            {
                try
                {
                    await CreateIndexesAsync(_current.IndexSpecs, cancellationToken);
                }
                catch (MongoCommandException)
                {
                    // One at a time, so only the refused ones are left out.
                    foreach (var spec in _current.IndexSpecs)
                    {
                        try
                        {
                            await CreateIndexesAsync([spec], cancellationToken);
                        }
                        catch (MongoCommandException ex)
                        {
                            Refused.Add(new RestoreAdjustment(_current.Name, spec.GetValue("name", "").ToString(), [], IndexRemoved: true, ServerMessage: ex.ErrorMessage));
                        }
                    }
                }
            }

            _current = null;
            _target = null;
        }

        private Task<BsonDocument> CreateIndexesAsync(IReadOnlyList<BsonDocument> specs, CancellationToken cancellationToken) =>
            database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "createIndexes", _current!.Name },
                { "indexes", new BsonArray(specs) },
            }, cancellationToken: cancellationToken);

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_batch.Count == 0)
            {
                return;
            }

            try
            {
                await _target!.InsertManyAsync(_batch, InsertOptions, cancellationToken);
                _documents += _batch.Count;
                progress.Replacing(_current!.Name, _collections, _documents);
            }
            finally
            {
                _batch.ForEach(document => document.Dispose());
                _batch.Clear();
            }
        }
    }
}
