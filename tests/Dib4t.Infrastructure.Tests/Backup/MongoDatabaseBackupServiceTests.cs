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

    [Fact]
    public async Task Replace_reports_each_collection_and_each_inserted_batch()
    {
        var database = mongo.NewDatabase();
        var file = DibbakTestWriter.Valid(
            ("tasks", [.. Enumerable.Range(0, 2001).Select(i => new BsonDocument("_id", i))]),
            ("empty", []),
            ("users", [new BsonDocument("_id", 1)])).ToFile();
        var progress = new MongoCompatibilityTests.RecordingProgress();
        using var stream = new MemoryStream(file);

        var refused = await Service(database).ReplaceAsync(stream, progress);

        refused.Should().BeEmpty();
        progress.Reports.Should().Equal(
            ("tasks", 1, 0), ("tasks", 1, 1000), ("tasks", 1, 2000), ("tasks", 1, 2001),
            ("empty", 2, 2001),
            ("users", 3, 2001), ("users", 3, 2002));
    }

    [Fact]
    public async Task Verification_of_a_restored_database_finds_no_difference()
    {
        var database = mongo.NewDatabase();
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame("users", indexes: """[{"v":2,"key":{"_id":1},"name":"_id_"},{"v":2,"unique":true,"key":{"email":1},"name":"email_1"}]""")
            .DocumentFrame(new BsonDocument { { "_id", 1 }, { "email", "a@dib4t.local" }, { "price", 10 } })
            .DocumentFrame(new BsonDocument { { "_id", 2 }, { "email", "b@dib4t.local" }, { "price", 10.5 } })
            .CollectionFrame("empty")
            .EndFrame(2, 2).ToFile();

        var (contents, verification) = await RestoreAndVerifyAsync(database, file, _ => Task.CompletedTask);

        contents.Collections.Select(c => (c.Name, c.Documents, c.Indexes.Count)).Should().Equal(("users", 2, 1), ("empty", 0, 0));
        verification.HasDifferences.Should().BeFalse();
        verification.Collections.Select(c => (c.Name, c.Exists, c.ExpectedDocuments, c.Documents, c.DocumentsMatch, c.MissingIndexes.Count))
            .Should().Equal(("users", true, 2L, 2L, true, 0), ("empty", true, 0L, 0L, true, 0));
        verification.UnexpectedCollections.Should().BeEmpty();
    }

    /// <summary>Every kind of difference the built-in verification must see, each made after the restore.</summary>
    [Fact]
    public async Task Verification_finds_every_kind_of_difference()
    {
        var database = mongo.NewDatabase();
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame("budgets").DocumentFrame(new BsonDocument { { "_id", 1 }, { "price", 10 } })
            .CollectionFrame("jobs").DocumentFrame(new BsonDocument("_id", 1)).DocumentFrame(new BsonDocument("_id", 2))
            .CollectionFrame("tasks").DocumentFrame(new BsonDocument { { "_id", 1 }, { "a", 1 }, { "b", 2 } })
            .CollectionFrame("users", indexes: """[{"v":2,"key":{"email":1},"name":"email_1"}]""").DocumentFrame(new BsonDocument("_id", 1))
            .CollectionFrame("gone")
            .EndFrame(5, 5).ToFile();

        var (_, verification) = await RestoreAndVerifyAsync(database, file, async db =>
        {
            // Same value, other BSON type: Int32 10 → Double 10.0.
            await db.GetCollection<BsonDocument>("budgets").ReplaceOneAsync(new BsonDocument("_id", 1), new BsonDocument { { "_id", 1 }, { "price", 10.0 } }, cancellationToken: Cancellation);
            await db.GetCollection<BsonDocument>("jobs").DeleteOneAsync(new BsonDocument("_id", 2), Cancellation);
            // Same fields and values, other order.
            await db.GetCollection<BsonDocument>("tasks").ReplaceOneAsync(new BsonDocument("_id", 1), new BsonDocument { { "_id", 1 }, { "b", 2 }, { "a", 1 } }, cancellationToken: Cancellation);
            await db.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", Cancellation);
            await db.DropCollectionAsync("gone", Cancellation);
            await db.CreateCollectionAsync("extra", cancellationToken: Cancellation);
        });

        verification.HasDifferences.Should().BeTrue();
        verification.Collections.Select(c => (c.Name, c.Exists, c.ExpectedDocuments, c.Documents, c.DocumentsMatch, string.Join(",", c.MissingIndexes)))
            .Should().Equal(
                ("budgets", true, 1L, 1L, false, ""),
                ("jobs", true, 2L, 1L, false, ""),
                ("tasks", true, 1L, 1L, false, ""),
                ("users", true, 1L, 1L, true, "email_1"),
                ("gone", false, 0L, 0L, false, ""));
        verification.UnexpectedCollections.Should().Equal("extra");
    }

    [Fact]
    public async Task What_the_server_still_refuses_is_left_out_and_every_document_is_written()
    {
        var database = mongo.NewDatabase();
        // Kept by the cleanup (known fields) but refused by the server: an unknown query operator, a key of 0.
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame(
                "jobs",
                options: """{"validator":{"state":{"$notAnOperator":1}}}""",
                indexes: """[{"v":2,"key":{"_id":1},"name":"_id_"},{"v":2,"key":{"a":0},"name":"a_0"},{"v":2,"key":{"b":1},"name":"b_1"}]""")
            .DocumentFrame(new BsonDocument { { "_id", 1 }, { "a", 1 }, { "b", 2 } })
            .EndFrame(1, 1).ToFile();
        using var stream = new MemoryStream(file);
        var service = Service(database);
        var contents = (await service.ValidateAsync(stream, Cancellation)).Value;

        var refused = await service.ReplaceAsync(stream, new MongoCompatibilityTests.RecordingProgress());
        var verification = await service.VerifyAsync(contents, Cancellation);

        refused.Select(r => (r.Collection, r.Index, string.Join(",", r.Removed), r.IndexRemoved)).Should().Equal(
            ("jobs", null, "validator", false), ("jobs", "a_0", "", true));
        refused.Should().OnlyContain(r => !string.IsNullOrEmpty(r.ServerMessage));
        (await database.GetCollection<BsonDocument>("jobs").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Cancellation)).Should().Be(1);
        (await (await database.GetCollection<BsonDocument>("jobs").Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation)).Select(i => i["name"].AsString)
            .Should().BeEquivalentTo("_id_", "b_1");
        verification.Collections.Single().Should().BeEquivalentTo(new { DocumentsMatch = true, MissingIndexes = new[] { "a_0" } }, "a refused index is a difference from the file");
    }

    private static async Task<(BackupContents Contents, RestoreVerification Verification)> RestoreAndVerifyAsync(
        IMongoDatabase database, byte[] file, Func<IMongoDatabase, Task> afterRestore)
    {
        using var stream = new MemoryStream(file);
        var service = Service(database);
        var contents = (await service.ValidateAsync(stream, Cancellation)).Value;
        await service.ReplaceAsync(stream, new MongoCompatibilityTests.RecordingProgress());
        await afterRestore(database);
        return (contents, await service.VerifyAsync(contents, Cancellation));
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
