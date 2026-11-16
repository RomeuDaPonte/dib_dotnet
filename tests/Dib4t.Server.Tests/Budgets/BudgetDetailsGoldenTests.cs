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
/// (Express's error page) is the D16 text. Decided differences with their own tests below: D8 (POST /api/task creates) and
/// D24 (the import adds the open budget's tasks/description into the highest revision of the typed number and never
/// deletes; of the legacy import cases only the refusals that D24 leaves as they are replay here).
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
                    if (section != "import" || IsUnchangedImportRefusal(Golden.GetProperty(section)[i]))
                    {
                        data.Add(section, i);
                    }
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

    [Fact]
    public async Task D24_import_adds_copies_of_the_open_budgets_tasks_to_the_highest_revision_and_replaces_its_description()
    {
        await ResetAsync();
        var target = ObjectId.Parse(Ids["{{source-r1}}"]); // 26-0003 R1: the highest revision; it has no tasks
        var copies = await OwnTasksAsync("{{budget}}", target);
        await Expected("tasks").InsertManyAsync(copies, cancellationToken: Cancellation);
        // 1 × 32.5 + 0.5 × 30 + 3 × 100 = 347.5, with its own margin 40: 347.5 + trunc(139) = 486.5
        await Expected("budgets").UpdateOneAsync(new BsonDocument("_id", target), new BsonDocument("$set", new BsonDocument
        {
            { "complete", false }, { "wasPdfDownloaded", false }, { "total", 486.5 }, { "description", "Fornecimento conforme especificação." },
        }), cancellationToken: Cancellation);

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Put, "/api/budget/import/" + Ids["{{budget}}"],
            """{"importDescription":true,"importTasks":true,"budgetNumber":"26-0003"}""", null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        JsonElement.Parse(answer.Text).GetProperty("_id").GetString().Should().Be(target.ToString(), "the receiving budget is answered");
        await DocumentsShouldMatchAsync("D24: copies added, total recomputed, description replaced, flags reset; nothing deleted, the open budget unchanged");
    }

    [Fact]
    public async Task D24_revision_0_adds_its_tasks_to_the_latest_revision_of_its_own_number()
    {
        await ResetAsync();
        var target = ObjectId.Parse(Ids["{{source-r1}}"]);
        await Expected("tasks").InsertManyAsync(await OwnTasksAsync("{{source}}", target), cancellationToken: Cancellation);
        // 32.5 + 15 + 577.5 − 2e21, margin 40 (JavaScript arithmetic)
        var tasksTotal = 32.5 + (0.5 * 30) + (15 * 38.5) + (-2 * 1e21);
        await Expected("budgets").UpdateOneAsync(new BsonDocument("_id", target), new BsonDocument("$set", new BsonDocument
        {
            { "complete", false }, { "wasPdfDownloaded", false }, { "total", tasksTotal + Math.Truncate(tasksTotal * 40 / 100) },
        }), cancellationToken: Cancellation);

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Put, "/api/budget/import/" + Ids["{{source}}"],
            """{"importDescription":false,"importTasks":true,"budgetNumber":"26-0003"}""", null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        await DocumentsShouldMatchAsync("D24: revision 0 into revision 1 of the same number");
    }

    [Fact]
    public async Task D24_a_missing_description_is_not_imported_and_the_flags_are_reset()
    {
        await ResetAsync();
        await Expected("budgets").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(Ids["{{budget}}"])),
            new BsonDocument("$set", new BsonDocument { { "complete", false }, { "wasPdfDownloaded", false } }), cancellationToken: Cancellation);

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Put, "/api/budget/import/" + Ids["{{imported}}"],
            """{"importDescription":true,"importTasks":false,"budgetNumber":"26-0007"}""", null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        await DocumentsShouldMatchAsync("the receiving budget keeps its description; only its flags change");
    }

    [Theory]
    [InlineData("{{budget}}", "26-0007", "Não é possível importar para o próprio orçamento.")]
    [InlineData("{{budget}}", "26-0004", "Orçamento gerou obra!")]
    [InlineData("{{job-budget}}", "26-0007", null)]
    public async Task D24_import_into_itself_or_into_a_budget_with_a_job_is_refused(string open, string number, string? refusal)
    {
        await ResetAsync();
        if (refusal is null)
        {
            // An open budget with a job may still send its tasks elsewhere (it does not change).
            var target = ObjectId.Parse(Ids["{{budget}}"]);
            await Expected("tasks").InsertManyAsync(await OwnTasksAsync(open, target), cancellationToken: Cancellation);
            // its own 3 tasks (347.5) + 2 × 28 = 403.5, margin 35: 403.5 + trunc(141.225) = 544.5
            await Expected("budgets").UpdateOneAsync(new BsonDocument("_id", target), new BsonDocument("$set", new BsonDocument
            {
                { "complete", false }, { "wasPdfDownloaded", false }, { "total", 544.5 }, { "description", "Fornecimento conforme especificação." },
            }), cancellationToken: Cancellation);
        }

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Put, "/api/budget/import/" + Ids[open],
            $$"""{"importDescription":true,"importTasks":true,"budgetNumber":"{{number}}"}""", null, Cancellation);

        if (refusal is null)
        {
            answer.Status.Should().Be(200, answer.Text);
        }
        else
        {
            (answer.Status, answer.Text).Should().Be((400, refusal));
        }

        await DocumentsShouldMatchAsync(refusal ?? "a budget with a job can send its tasks");
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

    /// <summary>
    /// Legacy import cases D24 leaves as they are: refusals from Joi, an unknown number and the open budget's id or
    /// absence (the job check now concerns the receiving budget, so legacy's job refusal is not among them).
    /// </summary>
    private static bool IsUnchangedImportRefusal(JsonElement item) =>
        item.GetProperty("status").GetInt32() != 200
        && !(item.TryGetProperty("text", out var text) && text.GetString() == "Orçamento gerou obra!");

    /// <summary>Copies of a stored budget's tasks for <paramref name="target"/>, as the import writes them (natural order, copy field order).</summary>
    private async Task<List<BsonDocument>> OwnTasksAsync(string budget, ObjectId target)
    {
        var own = await Expected("tasks").Find(new BsonDocument("budgetId", ObjectId.Parse(Ids[budget]))).ToListAsync(Cancellation);
        return [.. own.Select(task =>
        {
            var copy = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "taskType", task["taskType"] }, { "budgetId", target } };
            foreach (var field in new[] { "description", "unitPrice", "quantity" })
            {
                if (task.TryGetValue(field, out var value))
                {
                    copy[field] = value;
                }
            }

            copy["__v"] = 0;
            return copy;
        })];
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
