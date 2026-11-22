using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Dib4t.Infrastructure.Backup;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>The restore adapter against a real <c>mongo:9.0.8</c> (PLAN.md §7.6, §11.4).</summary>
public sealed class MongoDatabaseBackupServiceTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restore_replaces_every_user_collection_with_the_backup()
    {
        var database = mongo.NewDatabase();
        await database.GetCollection<BsonDocument>("old").InsertOneAsync(new BsonDocument("x", 1), cancellationToken: Cancellation);
        await database.GetCollection<BsonDocument>("budgets").InsertOneAsync(new BsonDocument("stale", true), cancellationToken: Cancellation);
        var budget = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "budgetNumber", "26-0001" } };

        var result = await RestoreAsync(database, DibbakTestWriter.Valid(("budgets", [budget]), ("empty", [])).ToFile());

        result.Value.Should().BeEquivalentTo(new { Collections = 2, Documents = 1L });
        (await CollectionNamesAsync(database)).Should().BeEquivalentTo("budgets", "empty");
        (await database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .Should().Equal(budget);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(2001)]
    public async Task Restore_inserts_every_document_across_batches_in_file_order(int count)
    {
        var database = mongo.NewDatabase();
        var documents = Enumerable.Range(0, count).Select(i => new BsonDocument { { "_id", i }, { "n", i * 1.5 } }).ToArray();

        var result = await RestoreAsync(database, DibbakTestWriter.Valid(("tasks", documents)).ToFile());

        result.Value.Documents.Should().Be(count);
        var stored = await database.GetCollection<BsonDocument>("tasks").Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument("$natural", 1)).ToListAsync(Cancellation);
        stored.Should().Equal(documents);
    }

    [Fact]
    public async Task Restore_creates_the_collection_options_and_indexes_of_the_backup()
    {
        var database = mongo.NewDatabase();
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame(
                "entities",
                options: """{"collation":{"locale":"pt"}}""",
                indexes: """[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.entities"},{"v":2,"unique":true,"key":{"name":1},"name":"name_1","ns":"dib4t.entities"}]""")
            .EndFrame(1, 0).ToFile();

        await RestoreAsync(database, file);

        var info = await (await database.ListCollectionsAsync(cancellationToken: Cancellation)).SingleAsync(Cancellation);
        info["options"]["collation"]["locale"].AsString.Should().Be("pt");
        var indexes = await (await database.GetCollection<BsonDocument>("entities").Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation);
        indexes.Select(i => i["name"].AsString).Should().BeEquivalentTo("_id_", "name_1");
        indexes.Single(i => i["name"] == "name_1")["unique"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task Rejected_backup_leaves_the_database_unchanged()
    {
        var database = mongo.NewDatabase();
        var existing = new BsonDocument { { "_id", 1 }, { "keep", true } };
        await database.GetCollection<BsonDocument>("budgets").InsertOneAsync(existing, cancellationToken: Cancellation);
        // Valid up to the last frame: the counts do not match.
        var file = new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("budgets").DocumentFrame(new BsonDocument("_id", 2)).EndFrame(1, 5).ToFile();

        var result = await RestoreAsync(database, file);

        result.Error.Should().Be(Error.Validation(BackupMessages.CountsMismatch));
        (await database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .Should().Equal(existing);
    }

    [Fact]
    public async Task Restore_keeps_views_and_system_collections_like_legacy()
    {
        var database = mongo.NewDatabase();
        await database.CreateCollectionAsync("budgets", cancellationToken: Cancellation);
        await database.CreateViewAsync<BsonDocument, BsonDocument>("budgetsView", "budgets", PipelineDefinition<BsonDocument, BsonDocument>.Create(Array.Empty<BsonDocument>()), cancellationToken: Cancellation);

        await RestoreAsync(database, DibbakTestWriter.Valid(("jobs", [])).ToFile());

        (await CollectionNamesAsync(database)).Should().Contain("budgetsView").And.Contain("jobs").And.NotContain("budgets");
    }

    /// <summary>The adapter on <paramref name="database"/>; the connection string only gives the info's host and port.</summary>
    internal static MongoDatabaseBackupService Service(IMongoDatabase database, string connectionString = "mongodb://localhost:27017/dib4t") =>
        new(database, Microsoft.Extensions.Options.Options.Create(new Dib4t.Infrastructure.Mongo.MongoOptions { ConnectionString = connectionString }));

    private static async Task<Result<RestoreSummary>> RestoreAsync(IMongoDatabase database, byte[] file)
    {
        using var stream = new MemoryStream(file);
        return await Service(database).RestoreAsync(stream, Cancellation);
    }

    private static async Task<List<string>> CollectionNamesAsync(IMongoDatabase database) =>
        (await (await database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation))
            .Where(name => !name.StartsWith("system.", StringComparison.Ordinal)).ToList();
}
