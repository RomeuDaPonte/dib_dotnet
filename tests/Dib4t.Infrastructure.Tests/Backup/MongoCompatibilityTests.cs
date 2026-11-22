using Dib4t.Application.Database;
using Dib4t.Infrastructure.Backup;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>
/// The 9.0 cleanup of collection options and index specs (PLAN.md §7.6 "Import of legacy backups"): what it keeps,
/// what it leaves out and reports, and, on a real <c>mongo:9.0.8</c>, the evidence for each rule (what the server
/// refuses without the cleanup) and a 4.1.4-shaped backup with every oddity restored with no difference.
/// </summary>
public sealed class MongoCompatibilityTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Options_9_0_knows_are_kept_in_their_order_and_the_rest_is_reported()
    {
        var adjustments = new List<RestoreAdjustment>();
        var options = BsonDocument.Parse("""
            { "flags": 1, "capped": true, "size": 4096, "max": 10, "temp": false, "validator": { "a": { "$exists": true } },
              "validationLevel": "moderate", "validationAction": "warn", "collation": { "locale": "pt", "version": "57.1" },
              "storageEngine": { "wiredTiger": {} }, "indexOptionDefaults": {}, "idIndex": { "key": { "_id": 1 }, "name": "_id_" },
              "uuid": "x", "recordPreImages": true, "viewOn": "a", "pipeline": [], "timeseries": {}, "encryptedFields": {},
              "changeStreamPreAndPostImages": { "enabled": false } }
            """);

        var kept = MongoCompatibility.CleanOptions("jobs", options, adjustments);

        kept.Names.Should().Equal("capped", "size", "max", "validator", "validationLevel", "validationAction", "collation",
            "storageEngine", "indexOptionDefaults", "changeStreamPreAndPostImages");
        adjustments.Should().BeEquivalentTo(new[] {new RestoreAdjustment("jobs", null,
            ["flags", "temp", "idIndex", "uuid", "recordPreImages", "viewOn", "pipeline", "timeseries", "encryptedFields"])}, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("""{ "size": 4096, "max": 10 }""", "size,max")]
    [InlineData("""{ "capped": false, "size": 4096 }""", "size")]
    [InlineData("""{ "capped": 0, "max": 4 }""", "max")]
    [InlineData("""{ "expireAfterSeconds": 60 }""", "expireAfterSeconds")]
    [InlineData("""{ "clusteredIndex": { "key": { "_id": 1 }, "unique": true }, "expireAfterSeconds": 60 }""", "")]
    [InlineData("""{ "capped": true, "size": 4096 }""", "")]
    [InlineData("{}", "")]
    public void Options_9_0_refuses_in_that_combination_are_left_out(string json, string removed)
    {
        var adjustments = new List<RestoreAdjustment>();

        MongoCompatibility.CleanOptions("a", BsonDocument.Parse(json), adjustments);

        adjustments.SelectMany(a => a.Removed).Should().Equal(removed.Length == 0 ? [] : removed.Split(','));
    }

    [Fact]
    public void Index_fields_9_0_knows_are_kept_and_the_rest_is_reported()
    {
        var adjustments = new List<RestoreAdjustment>();
        var spec = BsonDocument.Parse("""
            { "unique": true, "key": { "email": 1 }, "name": "email_1", "background": true, "dropDups": true, "safe": null,
              "sparse": true, "partialFilterExpression": { "email": { "$exists": true } }, "collation": { "locale": "pt" },
              "expireAfterSeconds": 3600, "hidden": false, "storageEngine": {}, "unknownOption": 1 }
            """);

        var kept = MongoCompatibility.CleanIndex("users", spec, adjustments);

        kept!.Names.Should().Equal("unique", "key", "name", "sparse", "partialFilterExpression", "collation", "expireAfterSeconds", "hidden", "storageEngine");
        adjustments.Should().BeEquivalentTo(new[] { new RestoreAdjustment("users", "email_1", ["background", "dropDups", "safe", "unknownOption"]) }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Text_geo_and_wildcard_index_options_are_kept()
    {
        var adjustments = new List<RestoreAdjustment>();
        var specs = new[]
        {
            """{ "key": { "_fts": "text", "_ftsx": 1 }, "name": "t", "weights": { "a": 1 }, "default_language": "portuguese", "language_override": "lang", "textIndexVersion": 3 }""",
            """{ "key": { "p": "2dsphere" }, "name": "p", "2dsphereIndexVersion": 3, "coarsestIndexedLevel": 0, "finestIndexedLevel": 30 }""",
            """{ "key": { "p": "2d" }, "name": "p2", "bits": 26, "min": -180, "max": 180 }""",
            """{ "key": { "$**": 1 }, "name": "w", "wildcardProjection": { "a": 1 } }""",
            """{ "key": { "a": 1 }, "name": "a", "prepareUnique": true }""",
        };

        foreach (var json in specs)
        {
            var spec = BsonDocument.Parse(json);
            MongoCompatibility.CleanIndex("a", spec, adjustments)!.Equals(spec).Should().BeTrue(json);
        }

        adjustments.Should().BeEmpty();
    }

    [Fact]
    public void A_geo_haystack_index_is_left_out_whole()
    {
        var adjustments = new List<RestoreAdjustment>();

        MongoCompatibility.CleanIndex("places", BsonDocument.Parse("""{ "key": { "pos": "geoHaystack", "type": 1 }, "name": "pos_geoHaystack_type_1", "bucketSize": 1 }"""), adjustments)
            .Should().BeNull();

        adjustments.Should().BeEquivalentTo(new[] { new RestoreAdjustment("places", "pos_geoHaystack_type_1", [], IndexRemoved: true) }, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("""{ "_fts": "text", "_ftsx": 1 }""", true)]
    [InlineData("""{ "p": "2dsphere" }""", true)]
    [InlineData("""{ "p": "2d" }""", true)]
    [InlineData("""{ "a": 1 }""", false)]
    [InlineData("""{ "a": "hashed" }""", false)]
    public void On_a_collection_with_a_collation_text_and_geo_indexes_get_the_simple_collation_they_were_built_with(string key, bool simple)
    {
        var adjustments = new List<RestoreAdjustment>();
        var spec = new BsonDocument { { "key", BsonDocument.Parse(key) }, { "name", "i" } };

        var kept = MongoCompatibility.CleanIndex("a", spec, adjustments, collectionHasCollation: true)!;

        kept.GetValue("collation", BsonNull.Value).Should().Be(simple ? new BsonDocument("locale", "simple") : BsonNull.Value);
        MongoCompatibility.CleanIndex("a", spec, adjustments)!.Contains("collation").Should().BeFalse("no collation on the collection: nothing to add");
        adjustments.Should().BeEmpty("the index is the same as in the source database");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    public void A_ttl_that_is_not_a_number_is_left_out(string ttl)
    {
        var adjustments = new List<RestoreAdjustment>();

        var kept = MongoCompatibility.CleanIndex("a", BsonDocument.Parse($$"""{ "key": { "d": 1 }, "name": "d_1", "expireAfterSeconds": {{ttl}} }"""), adjustments);

        kept!.Names.Should().Equal("key", "name");
        adjustments.Should().BeEquivalentTo(new[] { new RestoreAdjustment("a", "d_1", ["expireAfterSeconds"]) }, o => o.WithStrictOrdering());
    }

    /// <summary>The evidence for the rules: what <c>mongo:9.0.8</c> does with each left-out option without the cleanup.</summary>
    [Theory]
    [InlineData("""{ "temp": true }""")]
    [InlineData("""{ "uuid": "x" }""")]
    [InlineData("""{ "recordPreImages": true }""")]
    [InlineData("""{ "autoIndexId": true }""")]
    [InlineData("""{ "anUnknownOption": 1 }""")]
    [InlineData("""{ "size": 4096 }""")]
    [InlineData("""{ "expireAfterSeconds": 60 }""")]
    public async Task Mongo_9_0_refuses_the_collection_options_the_cleanup_leaves_out(string options)
    {
        var database = mongo.NewDatabase();
        var create = new BsonDocument("create", "a").AddRange(BsonDocument.Parse(options));

        var act = () => database.RunCommandAsync<BsonDocument>(create, cancellationToken: Cancellation);

        await act.Should().ThrowAsync<MongoCommandException>();
    }

    [Theory]
    [InlineData("""{ "key": { "a": 1 }, "name": "a_1", "safe": null }""")]
    [InlineData("""{ "key": { "a": 1 }, "name": "a_1", "unknownOption": 1 }""")]
    [InlineData("""{ "key": { "a": 1 }, "name": "a_1", "v": 0 }""")]
    [InlineData("""{ "key": { "d": 1 }, "name": "d_1", "expireAfterSeconds": NaN }""")]
    [InlineData("""{ "key": { "pos": "geoHaystack", "type": 1 }, "name": "h", "bucketSize": 1 }""")]
    public async Task Mongo_9_0_refuses_the_index_specs_the_cleanup_changes(string spec)
    {
        var database = mongo.NewDatabase();
        await database.CreateCollectionAsync("a", cancellationToken: Cancellation);

        var act = () => database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "createIndexes", "a" }, { "indexes", new BsonArray { BsonDocument.Parse(spec) } } }, cancellationToken: Cancellation);

        await act.Should().ThrowAsync<MongoCommandException>();
    }

    [Fact]
    public async Task A_backup_with_every_old_option_restores_on_9_0_with_its_documents_and_no_difference()
    {
        var database = mongo.NewDatabase();
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame(
                "budgets",
                options: """{"flags":1,"temp":false,"autoIndexId":true,"size":1000,"collation":{"locale":"pt","caseLevel":false,"caseFirst":"off","strength":3,"numericOrdering":false,"alternate":"non-ignorable","maxVariable":"punct","normalization":false,"backwards":false,"version":"57.1"}}""",
                indexes: """[{"v":1,"key":{"_id":1},"name":"_id_","ns":"dib4t.budgets"},{"v":1,"unique":true,"key":{"budgetNumber":1},"name":"budgetNumber_1","ns":"dib4t.budgets","background":true,"dropDups":true,"safe":null},{"v":2,"key":{"pos":"geoHaystack","type":1},"name":"pos_geoHaystack_type_1","ns":"dib4t.budgets","bucketSize":1},{"v":2,"key":{"_fts":"text","_ftsx":1},"name":"text","ns":"dib4t.budgets","weights":{"client":1},"default_language":"english","language_override":"language","textIndexVersion":3}]""")
            .DocumentFrame(new BsonDocument { { "_id", ObjectId.Parse("600000000000000000000001") }, { "budgetNumber", "18-0001" }, { "client", "Orçamento" } })
            .CollectionFrame("logs", options: """{"capped":true,"size":8192,"max":100}""")
            .DocumentFrame(new BsonDocument { { "_id", 1 }, { "t", 1.5 } })
            .EndFrame(2, 2).ToFile();
        using var stream = new MemoryStream(file);
        var service = MongoDatabaseBackupServiceTests.Service(database);

        var contents = (await service.ValidateAsync(stream, Cancellation)).Value;
        var refused = await service.ReplaceAsync(stream, new RecordingProgress());
        var verification = await service.VerifyAsync(contents, Cancellation);

        refused.Should().BeEmpty();
        contents.Adjustments.Should().BeEquivalentTo(new[] {
            new RestoreAdjustment("budgets", null, ["flags", "temp", "size"]),
            new RestoreAdjustment("budgets", "budgetNumber_1", ["background", "dropDups", "safe"]),
            new RestoreAdjustment("budgets", "pos_geoHaystack_type_1", [], IndexRemoved: true)}, o => o.WithStrictOrdering());
        verification.HasDifferences.Should().BeFalse(string.Join("; ", verification.Collections.Select(c => c.ToString())));
        var budgets = database.GetCollection<BsonDocument>("budgets");
        (await (await budgets.Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation)).Select(i => i["name"].AsString)
            .Should().BeEquivalentTo("_id_", "budgetNumber_1", "text");
        var collation = (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "budgets") }, Cancellation)).SingleAsync(Cancellation))["options"]["collation"];
        collation["locale"].AsString.Should().Be("pt");
        var capped = (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "logs") }, Cancellation)).SingleAsync(Cancellation))["options"];
        (capped["capped"].AsBoolean, capped["max"].ToInt32()).Should().Be((true, 100));
    }

    internal sealed class RecordingProgress : IRestoreProgress
    {
        public List<(string Collection, int Number, long Documents)> Reports { get; } = [];

        public void Replacing(string collection, int collectionNumber, long documents) => Reports.Add((collection, collectionNumber, documents));
    }
}
