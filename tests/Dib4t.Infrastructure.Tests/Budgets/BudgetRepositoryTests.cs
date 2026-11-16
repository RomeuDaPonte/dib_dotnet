using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Domain.Budgets;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Repositories;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Budgets;

/// <summary>
/// The budget and task writes and queries on mongo:9.0.8 against what the legacy code does (<c>budget-golden.json</c>,
/// from Mongoose's own documents and <c>$__delta</c>): every insert of create, clone and new revision and every task
/// copy gives the legacy document (BSON types and field order included); every edit and the legacy update applied to
/// the same stored document give the same document; the search patterns match and fail on MongoDB like the legacy
/// ones; lists, counts and populated authors behave like the legacy queries.
/// </summary>
public sealed class BudgetRepositoryTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";
    private const string UserId = "5f1e9a2b3c4d5e6f70830001";
    private const string User2Id = "5f1e9a2b3c4d5e6f70830002";

    /// <summary>Patterns JavaScript accepts but MongoDB's PCRE refuses: the legacy aggregate fails there too (legacy 500, new app 400).</summary>
    private static readonly string[] RefusedByMongo = ["\\k"];

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.BudgetGolden));
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Inserts_of_create_clone_and_new_revision_store_the_legacy_documents()
    {
        var database = Database();
        var repository = new MongoBudgetRepository(database);
        var compared = 0;
        var cases = Golden.GetProperty("create").EnumerateArray().Where(c => c.GetProperty("outcome").GetString() == "200").Select(c => c.GetProperty("stored"))
            .Concat(Golden.GetProperty("clone").EnumerateArray().Select(c => c.GetProperty("stored")))
            .Concat(Golden.GetProperty("newRevision").EnumerateArray().Select(c => c.GetProperty("stored")));
        foreach (var expected in cases)
        {
            var legacy = Parse(expected);
            var budget = await repository.InsertAsync(ToNewBudget(legacy), Cancellation);

            var stored = await Raw(database, MongoCollectionNames.Budgets).Find(new BsonDocument("_id", ObjectId.Parse(budget.Id))).SingleAsync(Cancellation);
            legacy["_id"] = stored["_id"];
            stored.ToJson(Canonical).Should().Be(legacy.ToJson(Canonical));
            budget.Version.Should().Be(0);
            compared++;
        }

        compared.Should().Be(17);
    }

    [Fact]
    public async Task The_default_task_and_the_task_copies_are_the_legacy_documents()
    {
        var database = Database();
        var tasks = new MongoBudgetTaskRepository(database);
        var budgetId = ObjectId.GenerateNewId().ToString();
        foreach (var item in Golden.GetProperty("defaultTask").EnumerateArray())
        {
            var task = await tasks.InsertDefaultAsync(budgetId, item.GetProperty("prices").GetProperty("automation").GetDouble(), Cancellation);
            var stored = await Raw(database, MongoCollectionNames.Tasks).Find(new BsonDocument("_id", ObjectId.Parse(task.Id))).SingleAsync(Cancellation);
            var legacy = Parse(item.GetProperty("stored"));
            legacy["_id"] = stored["_id"];
            legacy["budgetId"] = ObjectId.Parse(budgetId);
            stored.ToJson(Canonical).Should().Be(legacy.ToJson(Canonical));
        }

        // The clone/new revision sources (budget-golden.js sourceTasks), read through the repository, copied, compared.
        var source = ObjectId.GenerateNewId();
        await Raw(database, MongoCollectionNames.Tasks).InsertManyAsync(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "budgetId", source }, { "taskType", "automation" }, { "description", "" }, { "unitPrice", 32.5 }, { "quantity", 1 }, { "__v", 0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "taskType", "development" }, { "budgetId", source }, { "description", "Projeto" }, { "unitPrice", 30 }, { "quantity", 0.5 }, { "__v", 0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "budgetId", source }, { "taskType", "machining" }, { "unitPrice", 38.5 }, { "quantity", 15 }, { "__v", 0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "budgetId", source }, { "taskType", "others" }, { "description", "Cáfé  " }, { "unitPrice", 1e21 }, { "quantity", -2 } },
        ], cancellationToken: Cancellation);
        var copyOf = ObjectId.GenerateNewId().ToString();
        await tasks.InsertCopiesAsync(copyOf, await tasks.ListByBudgetAsync(source.ToString(), Cancellation), Cancellation);

        var copies = await Raw(database, MongoCollectionNames.Tasks).Find(new BsonDocument("budgetId", ObjectId.Parse(copyOf))).Sort(new BsonDocument("_id", 1)).ToListAsync(Cancellation);
        var legacyCopies = Golden.GetProperty("clone")[0].GetProperty("tasks").EnumerateArray().Select(Parse).ToList();
        copies.Should().HaveCount(legacyCopies.Count);
        foreach (var (ours, legacy) in copies.Zip(legacyCopies))
        {
            legacy["_id"] = ours["_id"];
            legacy["budgetId"] = ObjectId.Parse(copyOf);
            ours.ToJson(Canonical).Should().Be(legacy.ToJson(Canonical), "the stored description is copied as it is (no NFC, no trim)");
        }

        await tasks.InsertCopiesAsync(copyOf, [], Cancellation); // nothing to copy: nothing sent
    }

    [Fact]
    public async Task Edits_write_what_the_legacy_save_writes()
    {
        var database = Database();
        var budgets = Raw(database, MongoCollectionNames.Budgets);
        var repository = new MongoBudgetRepository(database);
        var compared = 0;
        foreach (var item in Golden.GetProperty("edit").EnumerateArray().Where(c => c.GetProperty("outcome").GetString() == "200"))
        {
            var body = item.GetProperty("body");
            if (!body.TryGetProperty("personInCharge", out var person))
            {
                continue; // D7: the new app refuses this body before the save (Dib4t.Application.Tests)
            }

            var (ours, legacy) = await TwoCopiesAsync(budgets, item.GetProperty("existing"));
            var madeBy = item.GetProperty("name").GetString()!.Contains("other person", StringComparison.Ordinal) ? User2Id : UserId;
            var saved = await repository.UpdateAsync(ours.ToString(), new BudgetEdit(madeBy, body.GetProperty("descriptive").GetString()!, person.GetString()!), Cancellation);
            if (item.GetProperty("update").ValueKind != JsonValueKind.Null)
            {
                await budgets.UpdateOneAsync(new BsonDocument("_id", legacy), Parse(item.GetProperty("update")), cancellationToken: Cancellation);
            }

            (await WithoutIdAsync(budgets, ours)).Should().Be(await WithoutIdAsync(budgets, legacy), item.GetProperty("name").GetString());
            saved!.Descriptive.Should().Be(body.GetProperty("descriptive").GetString());
            (saved.Complete, saved.WasPdfDownloaded).Should().Be((false, false));
            compared++;
        }

        compared.Should().Be(7);
        (await repository.UpdateAsync(ObjectId.GenerateNewId().ToString(), new BudgetEdit(UserId, "x", "y"), Cancellation)).Should().BeNull();
    }

    [Fact]
    public async Task Search_patterns_match_and_fail_on_mongodb_like_the_legacy_ones()
    {
        var database = Database();
        await SeedAsync(database);
        var repository = new MongoBudgetRepository(database);

        async Task<string[]?> NumbersAsync(string pattern) =>
            (await repository.SearchAsync(pattern, Cancellation)).Rows?.Select(r => $"{r.Budget.BudgetNumber} R{r.Budget.Revision}").ToArray();

        (await NumbersAsync("corti")).Should().Equal(["26-0002 R0"], "client name, any case");
        (await NumbersAsync("bruno")).Should().Equal(["26-0003 R1", "26-0003 R0"], "author name; number desc, revision desc (dedupe is the service's)");
        (await NumbersAsync("26-000[12]")).Should().Equal(["26-0002 R0", "26-0001 R0"], "the text is a regular expression");
        (await NumbersAsync("eng\\.ª")).Should().Equal(["26-0001 R0"], "person in charge");
        (await NumbersAsync("órfão")).Should().BeEmpty("a budget whose client no longer exists is left out ($unwind)");
        var rows = (await repository.SearchAsync("corti", Cancellation)).Rows!;
        (rows[0].ClientName, rows[0].MadeByName).Should().Be(("Cortiças Reunidas", "Ana Administradora"));
        var refused = await repository.SearchAsync("((", Cancellation);
        refused.Rows.Should().BeNull();
        refused.Refusal.Should().Contain("Regular expression is invalid");

        foreach (var item in Golden.GetProperty("search").EnumerateArray().Where(c => c.TryGetProperty("outcome", out _)))
        {
            var q = item.GetProperty("parsed").GetProperty("q").GetString()!;
            if (q.Contains('\0', StringComparison.Ordinal))
            {
                continue; // refused before the query (Dib4t.Application.Tests)
            }

            var legacyFails = item.GetProperty("outcome").GetString() == "500" || RefusedByMongo.Contains(q);
            ((await repository.SearchAsync(q, Cancellation)).Rows is null).Should().Be(legacyFails, q);
        }
    }

    [Fact]
    public async Task Lists_counts_revisions_and_authors_read_like_the_legacy_queries()
    {
        var database = Database();
        await SeedAsync(database);
        var repository = new MongoBudgetRepository(database);

        var newest = await repository.ListNewestAsync(200, Cancellation);
        var limited = await repository.ListNewestAsync(2, Cancellation);
        var revisions = await repository.ListRevisionsAsync("26-0003", Cancellation);
        var author = await repository.FindAuthorAsync(UserId, Cancellation);

        newest.Select(r => $"{r.Budget.BudgetNumber} R{r.Budget.Revision}").Should().Equal("26-0004 R0", "26-0003 R1", "26-0003 R0", "26-0002 R0", "26-0001 R0", "25-0100 R1", "25-0100 R0");
        newest[0].ClientName.Should().BeNull("the client no longer exists (legacy: a TypeError for the whole list)");
        newest[1].MadeByName.Should().Be("Bruno Silva");
        limited.Should().HaveCount(2, "legacy reads only the newest documents (limit 200)");
        revisions.Select(r => r.Revision).Should().Equal(1, 0);
        (await repository.CountRevisionsAsync("26-0003", Cancellation)).Should().Be(2);
        (await repository.CountNumbersSinceAsync(BudgetNumber.YearStart(2026), Cancellation)).Should().Be(5, "distinct numbers dated in 2026: 25-0100 has a 2026 revision");
        (await repository.NumberExistsAsync("26-0004", Cancellation)).Should().BeTrue();
        (await repository.NumberExistsAsync("26-0005", Cancellation)).Should().BeFalse();
        author.Should().BeEquivalentTo(new BudgetAuthor(UserId, "Ana Administradora", "ana@dib4t.local", ["canSeeOrcamentos", null], "Desenvolvimento", 0));
        (await repository.FindAuthorAsync(ObjectId.GenerateNewId().ToString(), Cancellation)).Should().BeNull();
        var old = (await repository.FindByIdAsync(newest[^1].Budget.Id, Cancellation))!;
        (old.HasGenerateJob, old.Validity, old.Margin, old.Total).Should().Be((false, 30, 30, null), "Mongoose defaults for an old document (§4.4)");
    }

    private IMongoDatabase Database()
    {
        MongoMappings.Register();
        return mongo.NewDatabase();
    }

    private static IMongoCollection<BsonDocument> Raw(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);

    /// <summary>A golden document with its id names (<c>{{client}}</c> …) replaced by ObjectIds.</summary>
    private static BsonDocument Parse(JsonElement golden) => BsonDocument.Parse(golden.GetRawText()
        .Replace("{{client}}", ClientId, StringComparison.Ordinal)
        .Replace("{{user2}}", User2Id, StringComparison.Ordinal)
        .Replace("{{user}}", UserId, StringComparison.Ordinal)
        .Replace("{{budget}}", "5f1e9a2b3c4d5e6f70810001", StringComparison.Ordinal)
        .Replace("{{new}}", "5f1e9a2b3c4d5e6f708100ff", StringComparison.Ordinal));

    private static NewBudget ToNewBudget(BsonDocument legacy)
    {
        string? Text(string field) => legacy.TryGetValue(field, out var value) ? value.AsString : null;
        double? Number(string field) => legacy.TryGetValue(field, out var value) ? value.ToDouble() : null;
        return new NewBudget(Number("revision")!.Value, legacy["client"].AsObjectId.ToString(), Text("descriptive"), Text("personInCharge"),
            legacy["madeBy"].AsObjectId.ToString(), Text("budgetNumber")!, Text("paymentMethod")!, Number("margin")!.Value, Number("total"),
            Number("daysNeededToDoTheWork")!.Value, Number("validity")!.Value, Text("description"), legacy["date"].ToUniversalTime());
    }

    private static async Task SeedAsync(IMongoDatabase database)
    {
        var ana = ObjectId.Parse(UserId);
        var bruno = ObjectId.Parse(User2Id);
        await Raw(database, MongoCollectionNames.Users).InsertManyAsync(
        [
            new BsonDocument { { "_id", ana }, { "roles", new BsonArray { "canSeeOrcamentos", BsonNull.Value } }, { "fieldOfCompetencies", "Desenvolvimento" }, { "name", "Ana Administradora" }, { "email", "ana@dib4t.local" }, { "password", "$2a$10$secret" }, { "__v", 0 } },
            new BsonDocument { { "_id", bruno }, { "name", "Bruno Silva" }, { "email", "bruno@dib4t.local" }, { "password", "$2a$10$secret" }, { "__v", 0 } },
        ], cancellationToken: Cancellation);
        var metal = ObjectId.Parse(ClientId);
        var cortica = ObjectId.GenerateNewId();
        await Raw(database, MongoCollectionNames.Entities).InsertManyAsync(
        [
            new BsonDocument { { "_id", metal }, { "name", "Metalúrgica do Ave" }, { "type", "Cliente" } },
            new BsonDocument { { "_id", cortica }, { "name", "Cortiças Reunidas" }, { "type", "Cliente" } },
        ], cancellationToken: Cancellation);
        var date = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        BsonDocument Budget(string number, int revision, ObjectId client, ObjectId author, string person, DateTime? at = null) => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "revision", revision }, { "complete", false }, { "wasPdfDownloaded", false }, { "margin", 35 },
            { "daysNeededToDoTheWork", 60 }, { "validity", 30 }, { "hasGenerateJob", false }, { "client", client }, { "descriptive", "Máquina " + number },
            { "personInCharge", person }, { "madeBy", author }, { "budgetNumber", number }, { "paymentMethod", "30 dias" }, { "total", 43.5 },
            { "date", at ?? date }, { "__v", 0 },
        };
        var old = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "client", metal }, { "madeBy", ana }, { "descriptive", "Antigo" }, { "budgetNumber", "25-0100" },
            { "paymentMethod", "30 dias" }, { "date", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
        };
        await Raw(database, MongoCollectionNames.Budgets).InsertManyAsync(
        [
            Budget("26-0001", 0, metal, ana, "Eng.ª Maria"),
            Budget("26-0002", 0, cortica, ana, "Sr. Paulo"),
            Budget("26-0003", 0, metal, bruno, "X"),
            Budget("26-0003", 1, metal, bruno, "X"),
            Budget("26-0004", 0, ObjectId.GenerateNewId(), ana, "órfão"),
            old,
            Budget("25-0100", 1, metal, ana, "Y", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), // a 2026 revision of a 2025 number
        ], cancellationToken: Cancellation);
    }

    private static async Task<(ObjectId Ours, ObjectId Legacy)> TwoCopiesAsync(IMongoCollection<BsonDocument> collection, JsonElement existing)
    {
        var ours = Parse(existing);
        var legacy = ours.DeepClone().AsBsonDocument;
        ours["_id"] = ObjectId.GenerateNewId();
        legacy["_id"] = ObjectId.GenerateNewId();
        await collection.InsertManyAsync([ours, legacy], cancellationToken: Cancellation);
        return (ours["_id"].AsObjectId, legacy["_id"].AsObjectId);
    }

    private static async Task<string> WithoutIdAsync(IMongoCollection<BsonDocument> collection, ObjectId id)
    {
        var document = await collection.Find(new BsonDocument("_id", id)).SingleAsync(Cancellation);
        document.Remove("_id");
        return document.ToJson(Canonical);
    }
}
