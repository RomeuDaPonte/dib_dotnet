using Dib4t.Infrastructure.Backup;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>The backup writer and the statistics of the adapter against a real <c>mongo:9.0.8</c> (legacy <c>writeBackup</c>/<c>getInfo</c>, PLAN.md §7.6).</summary>
public sealed class MongoBackupWriterTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static readonly DateTime CreatedAt = new(2026, 11, 22, 10, 15, 30, 123, DateTimeKind.Utc);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Collections_come_in_the_legacy_localeCompare_order_without_views_and_system_collections()
    {
        var database = mongo.NewDatabase();
        // The order Node 16 gives for ["users","Users","_tmp","a-b","a_b","ab","a1","A2","b","é","e","f","Z","10","9","system2"].sort(localeCompare).
        string[] expected = ["_tmp", "10", "9", "a_b", "a-b", "a1", "A2", "ab", "b", "e", "é", "f", "system2", "users", "Users", "Z"];
        foreach (var name in expected.Reverse())
        {
            await database.CreateCollectionAsync(name, cancellationToken: Cancellation);
        }

        await database.CreateViewAsync("view", "b", new EmptyPipelineDefinition<BsonDocument>(), cancellationToken: Cancellation);

        var backup = DibbakFile.Parse(await BackupAsync(database));
        var info = await MongoDatabaseBackupServiceTests.Service(database).GetInfoAsync(Cancellation);

        backup.Collections.Select(c => c.Name).Should().Equal(expected);
        backup.Header.GetProperty("collections").EnumerateArray().Select(c => c.GetString()).Should().Equal(expected);
        info.CollectionsInfo.Select(c => c.Name).Should().Equal(expected);
        info.Collections.Should().Be(expected.Length);
    }

    [Fact]
    public async Task Documents_are_the_raw_bytes_in_natural_order_and_the_frames_are_the_legacy_ones()
    {
        var database = mongo.NewDatabase();
        var documents = Enumerable.Range(0, 2_500).Select(i => new BsonDocument { { "_id", 2_500 - i }, { "n", i * 1.5 }, { "i", i } }).ToArray();
        await database.GetCollection<BsonDocument>("tasks").InsertManyAsync(documents, cancellationToken: Cancellation);
        await database.GetCollection<BsonDocument>("empty").InsertOneAsync(new BsonDocument("_id", 1), cancellationToken: Cancellation);
        await database.GetCollection<BsonDocument>("empty").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);

        var service = MongoDatabaseBackupServiceTests.Service(database);
        using var output = new MemoryStream();
        var summary = await service.WriteBackupAsync(output, null, CreatedAt, Cancellation);
        var backup = DibbakFile.Parse(output.ToArray());

        summary.Should().Be(new Application.Database.BackupSummary(2, 2_500));
        backup.Header.GetRawText().Should().Be(
            $$"""{"format":"dib4t-backup","version":1,"createdAt":"2026-11-22T10:15:30.123Z","createdBy":null,"database":"{{database.DatabaseNamespace.DatabaseName}}","serverVersion":"9.0.8","collections":["empty","tasks"]}""");
        backup.Collections[0].Meta.GetRawText().Should().Be("""{"name":"empty","options":{},"indexes":[{"v":2,"key":{"_id":1},"name":"_id_"}],"count":0}""");
        backup.Collections[1].Meta.GetProperty("count").GetRawText().Should().Be("2500");
        backup.Collections[1].Documents.Select(d => new RawBsonDocument(d).ToBsonDocument()).Should().Equal(documents, "natural (insertion) order, not _id order");
        backup.Collections[1].Documents.Should().BeEquivalentTo(documents.Select(d => d.ToBson()), o => o.WithStrictOrdering(), "the server's bytes");
        (await DibbakFormatShouldReadAsync(output.ToArray())).Should().Be(2_500);
    }

    [Fact]
    public async Task The_backup_restores_with_the_restore_of_the_new_app()
    {
        var source = mongo.NewDatabase();
        await source.CreateCollectionAsync("capped", new CreateCollectionOptions { Capped = true, MaxSize = 8192 }, Cancellation);
        await source.GetCollection<BsonDocument>("capped").InsertOneAsync(new BsonDocument { { "_id", 1 }, { "d", new Decimal128(2.5m) }, { "l", 3L } }, cancellationToken: Cancellation);
        await source.GetCollection<BsonDocument>("entities").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("name"), new CreateIndexOptions { Unique = true, Name = "name_1" }), cancellationToken: Cancellation);
        await source.GetCollection<BsonDocument>("entities").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "ç" } }, cancellationToken: Cancellation);
        var target = mongo.NewDatabase();

        var restored = await MongoDatabaseBackupServiceTests.Service(target).RestoreAsync(new MemoryStream(await BackupAsync(source)), Cancellation);

        restored.Value!.Documents.Should().Be(2);
        foreach (var name in new[] { "capped", "entities" })
        {
            (await target.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
                .Should().Equal(await source.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation));
        }

        var options = (await (await target.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "capped") }, Cancellation)).SingleAsync(Cancellation))["options"];
        options["capped"].AsBoolean.Should().BeTrue();
        var indexes = await (await target.GetCollection<BsonDocument>("entities").Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation);
        indexes.Single(i => i["name"] == "name_1")["unique"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task After_a_failure_the_client_never_gets_a_well_formed_gzip_file()
    {
        var database = mongo.NewDatabase();
        await database.GetCollection<BsonDocument>("tasks").InsertManyAsync(
            Enumerable.Range(0, 5_000).Select(i => new BsonDocument { { "_id", i }, { "text", Guid.NewGuid().ToString() } }), cancellationToken: Cancellation);
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        using var output = new FailAfterStream(aborted, bytes: 1_000);

        var backup = () => MongoDatabaseBackupServiceTests.Service(database).WriteBackupAsync(output, null, CreatedAt, aborted.Token);

        await backup.Should().ThrowAsync<OperationCanceledException>();
        output.Written.Should().BeGreaterThan(1_000, "part of the file was sent");
        var validate = () => MongoDatabaseBackupService.CheckAsync(new MemoryStream(output.ToArray()), Cancellation);
        (await validate.Should().ThrowAsync<BackupFormatException>("no gzip trailer (and no end frame): never a complete-looking file"))
            .Which.Message.Should().Be("O ficheiro não é um backup válido da Dib4t.");
    }

    [Fact]
    public async Task Info_reports_dbStats_buildInfo_and_collStats_and_the_connection_host_and_port()
    {
        var database = mongo.NewDatabase();
        await database.GetCollection<BsonDocument>("jobs").InsertManyAsync([new BsonDocument("a", 1), new BsonDocument("a", 2)], cancellationToken: Cancellation);
        await database.CreateCollectionAsync("empty", cancellationToken: Cancellation);

        var info = await MongoDatabaseBackupServiceTests.Service(database, "mongodb://db.example:27019,other:1/dib4t").GetInfoAsync(Cancellation);

        var stats = await database.RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1), cancellationToken: Cancellation);
        (info.Name, info.Host, info.Port, info.ServerVersion).Should().Be((database.DatabaseNamespace.DatabaseName, "db.example", 27019, "9.0.8"));
        (info.Documents, info.Indexes, info.DataSize).Should().Be((2, 2, stats["dataSize"].ToDouble()));
        info.CollectionsInfo.Select(c => (c.Name, c.Count, c.Indexes)).Should().Equal(("empty", 0, 1), ("jobs", 2, 1));
        info.CollectionsInfo[0].AvgObjSize.Should().Be(0, "collStats has none for an empty collection: legacy `|| 0`");
        info.CollectionsInfo[1].AvgObjSize.Should().BeGreaterThan(0);
        info.CollectionsInfo.Should().OnlyContain(c => c.StorageSize > 0 && c.IndexSize > 0 && c.Size != null);
    }

    private static async Task<byte[]> BackupAsync(IMongoDatabase database)
    {
        using var output = new MemoryStream();
        await MongoDatabaseBackupServiceTests.Service(database).WriteBackupAsync(output, "ana.admin@dib4t.local", CreatedAt, Cancellation);
        return output.ToArray();
    }

    /// <summary>The app's own reader accepts the file (the format check of the restore).</summary>
    private static async Task<long> DibbakFormatShouldReadAsync(byte[] file) =>
        (await MongoDatabaseBackupService.CheckAsync(new MemoryStream(file), Cancellation)).Documents;

    /// <summary>Cancels the backup once more than <c>bytes</c> reached it (a browser that goes away mid-download).</summary>
    private sealed class FailAfterStream(CancellationTokenSource aborted, int bytes) : MemoryStream
    {
        public long Written => Length;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (Length > bytes)
            {
                await aborted.CancelAsync();
            }
        }
    }
}
