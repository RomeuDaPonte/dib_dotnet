using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Budgets;

/// <summary>
/// Every case of <c>budget-details-golden.json</c> (the legacy task routes, <c>patchProperty</c> and <c>import</c>,
/// run verbatim in Node) replayed over HTTP on mongo:9.0.8, from the same stored documents: the same status and text,
/// or the same JSON (without key order and without the password hash legacy sends in a populated author); and the
/// same documents afterwards, BSON-for-BSON (types and field order), against the legacy save's own
/// <c>$set</c>/<c>$unset</c>, inserts and deletes applied to a copy of the documents on the same server. A legacy 500
/// (Express's error page) is the D16 text. Decided difference: D8 (POST /api/task creates) has its own test below. D24
/// (importing from the budget's own number deletes its tasks) is kept as legacy: a golden case like the others.
/// </summary>
public sealed partial class BudgetDetailsGoldenTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string NewId = "{{new}}";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.BudgetDetailsGolden));

    private static readonly Dictionary<string, string> Ids = Golden.GetProperty("ids").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Names = Ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var section in new[] { "listTasks", "defaultTask", "updateTask", "deleteTask", "patch", "import" })
            {
                for (var i = 0; i < Golden.GetProperty(section).GetArrayLength(); i++)
                {
                    data.Add(section, i);
                }
            }

            return data;
        }
    }

    public static TheoryData<int> NewTaskCases => [.. Enumerable.Range(0, Golden.GetProperty("newTask").GetArrayLength())];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_legacy_case_gives_the_same_answer_and_documents(string section, int index)
    {
        var item = Golden.GetProperty(section)[index];
        await ResetAsync();
        foreach (var effect in item.GetProperty("effects").EnumerateArray())
        {
            await ApplyLegacyEffectAsync(effect);
        }

        var (method, path, body) = Request(section, item);
        var answer = await LegacyHttp.SendAsync(_client, method, path, body, null, Cancellation);

        var because = $"{section} #{index}: {method} {path} {body}";
        AnswerShouldMatch(item, answer, because);
        await DocumentsShouldMatchAsync(because);
    }

    [Theory]
    [MemberData(nameof(NewTaskCases))]
    public async Task D8_post_task_applies_the_intended_schema(int index)
    {
        var item = Golden.GetProperty("newTask")[index];
        item.GetProperty("legacy").GetProperty("status").GetInt32().Should().Be(400, "legacy refuses every body");
        await ResetAsync();
        var body = Bind(item.GetProperty("body").GetString()!);
        var intended = item.GetProperty("intended");

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/task", body, null, Cancellation);

        if (intended.GetProperty("message").GetString() is { } message)
        {
            (answer.Status, answer.Text).Should().Be((400, message), body);
            await DocumentsShouldMatchAsync(body);
            return;
        }

        var budgetId = JsonElement.Parse(body).GetProperty("budgetId").GetString()!;
        if (budgetId == Ids["{{missing}}"] || budgetId == Ids["{{job-budget}}"])
        {
            (answer.Status, answer.Text).Should().Be((400, budgetId == Ids["{{missing}}"] ? "Orçamento não existe" : "Orçamento gerou obra!"), body);
            await DocumentsShouldMatchAsync(body);
            return;
        }

        answer.Status.Should().Be(200, body);
        var stored = Document(intended.GetProperty("stored"));
        var created = await app.Database.GetCollection<BsonDocument>("tasks").Find(new BsonDocument("_id", ObjectId.Parse(JsonElement.Parse(answer.Text).GetProperty("_id").GetString()!)))
            .SingleAsync(Cancellation);
        created["_id"] = stored["_id"] = NewId;
        Sorted(created).ToJson(Canonical).Should().Be(Sorted(stored).ToJson(Canonical), "the values Mongoose would store (field order: the default task's)");
        created.Names.Should().Equal(stored.Names.OrderBy(n => n switch { "_id" => 0, "budgetId" => 1, "taskType" => 2, "description" => 3, "unitPrice" => 4, "quantity" => 5, _ => 6 }));
        var budget = await app.Database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument("_id", ObjectId.Parse(Ids["{{budget}}"]))).SingleAsync(Cancellation);
        (budget["complete"].AsBoolean, budget["wasPdfDownloaded"].AsBoolean).Should().Be((false, false), "the budget is marked incomplete, like the edit");
    }

    private static (HttpMethod Method, string Path, string? Body) Request(string section, JsonElement item)
    {
        string Param(string name) => Bind(item.GetProperty(name).GetString()!);
        string? Body() => item.TryGetProperty("body", out var body) ? Bind(body.GetString()!) : null;
        return section switch
        {
            "listTasks" => (HttpMethod.Get, "/api/task/getBudgetTasks/" + Param("budgetId"), null),
            "defaultTask" => (HttpMethod.Post, "/api/task/createDefaultTask/" + Param("budgetId"), null),
            "updateTask" => (HttpMethod.Put, $"/api/task/{Param("taskId")}/{Param("budgetId")}", Body()),
            "deleteTask" => (HttpMethod.Delete, $"/api/task/{Param("taskId")}/{Param("budgetId")}", null),
            "patch" => (HttpMethod.Put, "/api/budget/patchProperty/" + Param("budgetId"), Body()),
            "import" => (HttpMethod.Put, "/api/budget/import/" + Param("budgetId"), Body()),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
    }

    private static void AnswerShouldMatch(JsonElement item, (int Status, string ContentType, string Text) answer, string because)
    {
        if (item.TryGetProperty("error", out _))
        {
            (answer.Status, answer.Text).Should().Be((500, "Something failed."), "D16: " + because);
            return;
        }

        answer.Status.Should().Be(item.GetProperty("status").GetInt32(), because);
        if (item.TryGetProperty("text", out var text))
        {
            answer.ContentType.Should().Be("text/html; charset=utf-8", because);
            answer.Text.Should().Be(text.GetString(), because);
            return;
        }

        answer.ContentType.Should().Be("application/json; charset=utf-8", because);
        // §4.3 rule 2: the new app writes NFC (legacy stores a decomposed text as sent).
        var expected = JsonNode.Parse(item.GetProperty("json").GetRawText().Normalize(System.Text.NormalizationForm.FormC))!;
        RemovePassword(expected);
        var actual = JsonNode.Parse(IdPattern().Replace(answer.Text, m => "\"" + (Names.GetValueOrDefault(m.Groups[1].Value) ?? NewId) + "\""))!;
        Sorted(actual).ToJsonString().Should().Be(Sorted(expected).ToJsonString(), because);
    }

    private async Task ResetAsync()
    {
        foreach (var name in new[] { "budgets", "tasks", "entities", "prices", "x-budgets", "x-tasks" })
        {
            await app.Database.DropCollectionAsync(name, Cancellation);
        }

        var budgets = Golden.GetProperty("budgets").EnumerateArray().Select(Document).ToList();
        var tasks = Golden.GetProperty("tasks").EnumerateArray().Select(Document).ToList();
        await Collection("budgets").InsertManyAsync(budgets, cancellationToken: Cancellation);
        await Expected("budgets").InsertManyAsync(budgets.Select(b => b.DeepClone().AsBsonDocument), cancellationToken: Cancellation);
        await Collection("tasks").InsertManyAsync(tasks, cancellationToken: Cancellation);
        await Expected("tasks").InsertManyAsync(tasks.Select(t => t.DeepClone().AsBsonDocument), cancellationToken: Cancellation);
        await Collection("entities").InsertOneAsync(Document(Golden.GetProperty("entity")), cancellationToken: Cancellation);
        var user = Document(Golden.GetProperty("user"));
        await Collection("users").ReplaceOneAsync(new BsonDocument("_id", user["_id"]), user, new ReplaceOptions { IsUpsert = true }, Cancellation);
        var prices = BsonDocument.Parse(Golden.GetProperty("prices").GetRawText());
        prices.InsertAt(0, new BsonElement("_id", ObjectId.GenerateNewId()));
        await Collection("prices").InsertOneAsync(prices, cancellationToken: Cancellation);
    }

    /// <summary>The legacy save's effect, applied to the copy (<c>x-…</c>) of the documents.</summary>
    private async Task ApplyLegacyEffectAsync(JsonElement effect)
    {
        if (effect.TryGetProperty("update", out var update) && effect.TryGetProperty("_id", out var id))
        {
            if (update.ValueKind != JsonValueKind.Null)
            {
                var collection = IsTask(id) ? "tasks" : "budgets";
                await Expected(collection).UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(Bind(id.GetProperty("$oid").GetString()!))),
                    Nfc(BsonDocument.Parse(Bind(update.GetRawText()))), cancellationToken: Cancellation);
            }

            return;
        }

        if (effect.TryGetProperty("insert", out var collectionName))
        {
            await Expected(collectionName.GetString()!).InsertOneAsync(Document(effect.GetProperty("document")), cancellationToken: Cancellation);
        }
        else if (effect.TryGetProperty("insertMany", out var documents))
        {
            if (documents.GetArrayLength() > 0)
            {
                await Expected("tasks").InsertManyAsync(documents.EnumerateArray().Select(Document), cancellationToken: Cancellation);
            }
        }
        else if (effect.TryGetProperty("deleteMany", out var filter))
        {
            await Expected("tasks").DeleteManyAsync(Document(filter), Cancellation);
        }
        else if (effect.TryGetProperty("deleteOne", out var one))
        {
            await Expected("tasks").DeleteOneAsync(Document(one), Cancellation);
        }
        else
        {
            throw new InvalidOperationException("unknown legacy effect " + effect.GetRawText());
        }
    }

    private static bool IsTask(JsonElement id) =>
        Golden.GetProperty("tasks").EnumerateArray().Any(t => t.GetProperty("_id").GetProperty("$oid").GetString() == id.GetProperty("$oid").GetString());

    private async Task DocumentsShouldMatchAsync(string because)
    {
        foreach (var name in new[] { "budgets", "tasks" })
        {
            var actual = await Collection(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            var expected = await Expected(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            actual.Select(Masked).Should().Equal(expected.Select(Masked), $"{name} after {because}");
        }
    }

    /// <summary>A document's canonical Extended JSON (types and field order), a generated <c>_id</c> as <c>{{new}}</c>.</summary>
    private static string Masked(BsonDocument document)
    {
        var copy = document.DeepClone().AsBsonDocument;
        if (!Names.ContainsKey(copy["_id"].ToString()!))
        {
            copy["_id"] = NewId;
        }

        return copy.ToJson(Canonical);
    }

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    private IMongoCollection<BsonDocument> Expected(string name) => app.Database.GetCollection<BsonDocument>("x-" + name);

    /// <summary>A golden document (canonical Extended JSON with id names) as BSON; <c>{{new}}</c> ids get a fresh ObjectId.</summary>
    private static BsonDocument Document(JsonElement json) =>
        BsonDocument.Parse(Bind(json.GetRawText()).Replace("\"" + NewId + "\"", "\"" + ObjectId.GenerateNewId() + "\"", StringComparison.Ordinal));

    private static string Bind(string text) =>
        NamePattern().Replace(text, m => Ids.TryGetValue(m.Value, out var hex) ? hex : m.Value);

    /// <summary>The texts of a legacy <c>$set</c> as the new app writes them: NFC (PLAN.md §4.3 rule 2).</summary>
    private static BsonDocument Nfc(BsonDocument update)
    {
        if (update.TryGetValue("$set", out var set))
        {
            foreach (var element in set.AsBsonDocument.ToList())
            {
                if (element.Value.IsString)
                {
                    set.AsBsonDocument[element.Name] = element.Value.AsString.Normalize(System.Text.NormalizationForm.FormC);
                }
            }
        }

        return update;
    }

    private static void RemovePassword(JsonNode node)
    {
        if (node is JsonObject obj && obj["madeBy"] is JsonObject madeBy)
        {
            madeBy.Remove("password");
        }
    }

    private static JsonNode Sorted(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(e => e is null ? null : Sorted(e))]),
        _ => JsonNode.Parse(node.ToJsonString())!,
    };

    private static BsonDocument Sorted(BsonDocument document) => new(document.OrderBy(e => e.Name, StringComparer.Ordinal));

    [GeneratedRegex(@"\{\{[a-z0-9-]+\}\}")]
    private static partial Regex NamePattern();

    [GeneratedRegex("\"([0-9a-f]{24})\"")]
    private static partial Regex IdPattern();
}
