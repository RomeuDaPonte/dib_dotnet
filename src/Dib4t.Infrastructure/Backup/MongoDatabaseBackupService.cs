using Dib4t.Application.Common;
using Dib4t.Application.Database;
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
/// Not yet here (PLAN.md §10 Phase 5): cleaning options/index specs that MongoDB 9.0 rejects, the automatic
/// pre-restore backup and the built-in verification.
/// </remarks>
internal sealed class MongoDatabaseBackupService(IMongoDatabase database) : IDatabaseBackupService
{
    public const int InsertBatchSize = 1000;

    public async Task<Result<RestoreSummary>> RestoreAsync(Stream backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);

        DibbakSummary summary;
        try
        {
            summary = await ValidateAsync(backup, cancellationToken);
        }
        catch (BackupFormatException ex)
        {
            return Error.Validation(ex.Message);
        }

        // From here on the database is being replaced: a cancelled request (closed browser) must not leave it half
        // restored, so the restore runs to the end (legacy behavior as well).
        var restoring = CancellationToken.None;
        await DropUserCollectionsAsync(restoring);

        backup.Position = 0;
        await using var content = new VerifiedGzipReadStream(backup);
        var writer = new RestoreWriter(database);
        await DibbakReader.ReadAsync(content, writer, validateDocuments: false, restoring);
        await writer.CompleteAsync(restoring);

        return new RestoreSummary(summary.CreatedAt, summary.Database, summary.Collections, summary.Documents);
    }

    /// <summary>First pass: gunzip and check the whole file; nothing is written.</summary>
    /// <exception cref="BackupFormatException">The file is not a valid backup (legacy message).</exception>
    internal static async Task<DibbakSummary> ValidateAsync(Stream backup, CancellationToken cancellationToken)
    {
        backup.Position = 0;
        await using var content = new VerifiedGzipReadStream(backup);
        try
        {
            return await DibbakReader.ReadAsync(content, visitor: null, validateDocuments: true, cancellationToken);
        }
        catch (BackupFormatException)
        {
            // Legacy gunzips the whole file before it parses it: a damaged gzip stream wins over a format error.
            await content.CopyToAsync(Stream.Null, cancellationToken);
            throw;
        }
    }

    /// <summary>Legacy <c>listUserCollections</c>: collections (not views) whose name does not start with <c>system.</c>.</summary>
    private async Task DropUserCollectionsAsync(CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        var collections = await cursor.ToListAsync(cancellationToken);
        foreach (var collection in collections)
        {
            var name = collection["name"].AsString;
            var type = collection.GetValue("type", BsonNull.Value);
            if ((type.IsBsonNull || type == "collection") && !name.StartsWith("system.", StringComparison.Ordinal))
            {
                await database.DropCollectionAsync(name, cancellationToken);
            }
        }
    }

    private sealed class RestoreWriter(IMongoDatabase database) : IDibbakVisitor
    {
        private static readonly InsertManyOptions InsertOptions = new() { IsOrdered = true, BypassDocumentValidation = true };

        private readonly List<RawBsonDocument> _batch = new(InsertBatchSize);
        private DibbakCollection? _current;
        private IMongoCollection<RawBsonDocument>? _target;

        public async ValueTask OnCollectionAsync(DibbakCollection collection, CancellationToken cancellationToken)
        {
            await CompleteAsync(cancellationToken);

            var create = new BsonDocument("create", collection.Name);
            foreach (var option in collection.CreateOptions)
            {
                create[option.Name] = option.Value; // like the legacy `cmd[n] = options[n]`
            }

            await database.RunCommandAsync<BsonDocument>(create, cancellationToken: cancellationToken);
            _current = collection;
            _target = database.GetCollection<RawBsonDocument>(collection.Name);
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
                var command = new BsonDocument
                {
                    { "createIndexes", _current.Name },
                    { "indexes", new BsonArray(_current.IndexSpecs) },
                };
                await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
            }

            _current = null;
            _target = null;
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_batch.Count == 0)
            {
                return;
            }

            try
            {
                await _target!.InsertManyAsync(_batch, InsertOptions, cancellationToken);
            }
            finally
            {
                _batch.ForEach(document => document.Dispose());
                _batch.Clear();
            }
        }
    }
}
