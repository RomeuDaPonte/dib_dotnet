using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dib4t.Server.Tests.Budgets;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every case of <c>job-golden.json</c> (the legacy <c>POST /api/job</c> handler run verbatim in Node: Joi, the
/// repositories' lookups, Mongoose casts and saves) replayed over HTTP on mongo:9.0.8 from the same stored documents,
/// with the same clock: the same status and text, or the same JSON (without key order and without the password hash
/// legacy sends in the populated author); and the same documents afterwards, BSON-for-BSON (types and field order),
/// against the legacy save's own <c>$set</c> and insert applied to a copy on the same server. A legacy 500 (Express's
/// error page) is the D16 text. The decided differences are stated in <see cref="Difference"/> and checked explicitly.
/// </summary>
public sealed partial class JobCreateGoldenTests(AppWithGoldenClock app) : IClassFixture<AppWithGoldenClock>
{
    private const string NewId = "{{new}}";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobGolden));

    private static readonly Dictionary<string, string> Ids = Golden.GetProperty("ids").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Names = Ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);

    /// <summary>How the new app's answer to a golden case differs from legacy (PLAN.md §9, §10 Phase 4).</summary>
    public enum Difference
    {
        None,

        /// <summary>
        /// D26: Joi accepts the body, the legacy save of the job refuses it ("TRUE" as the flag, a task without
        /// quantity) after the budget was marked: here a 500 with nothing written.
        /// </summary>
        D26NothingWritten,

        /// <summary>A date text only V8's fallback parser reads (<c>JsDate</c>): the Joi 400 instead of a job.</summary>
        V8OnlyDateText,

        /// <summary>A date beyond the year 9999 (JavaScript allows up to 275760): a 500 with nothing written.</summary>
        DateBeyondYear9999,
    }

    public static TheoryData<string, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var section in new[] { "create", "shapes" })
            {
                for (var i = 0; i < Golden.GetProperty(section).GetArrayLength(); i++)
                {
                    data.Add(section, i);
                }
            }

            return data;
        }
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_legacy_case_gives_the_same_answer_and_documents(string section, int index)
    {
        var item = Golden.GetProperty(section)[index];
        var body = Bind(item.GetProperty("body").GetString()!);
        var difference = DifferenceOf(item);
        await ResetAsync();
        if (difference == Difference.None)
        {
            foreach (var effect in item.GetProperty("effects").EnumerateArray())
            {
                await ApplyLegacyEffectAsync(effect);
            }
        }

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", body, null, Cancellation);

        var because = $"{section} #{index}: {body}";
        switch (difference)
        {
            case Difference.None:
                AnswerShouldMatch(item, answer, because);
                break;
            case Difference.D26NothingWritten or Difference.DateBeyondYear9999:
                item.GetProperty("status").GetInt32().Should().Be(difference == Difference.D26NothingWritten ? 500 : 200, because);
                (answer.Status, answer.Text).Should().Be((500, "Something failed."), because);
                break;
            case Difference.V8OnlyDateText:
                item.GetProperty("status").GetInt32().Should().Be(200, because);
                (answer.Status, answer.Text).Should().Be((400, "\"dueDate\" must be a number of milliseconds or valid date string"), because);
                break;
        }

        await DocumentsShouldMatchAsync(because);
    }

    [Fact]
    public void Every_decided_difference_occurs_in_the_golden_file()
    {
        var cases = Golden.GetProperty("create").EnumerateArray().Concat(Golden.GetProperty("shapes").EnumerateArray()).ToList();
        cases.GroupBy(DifferenceOf).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(new Dictionary<Difference, int>
        {
            [Difference.None] = cases.Count - 7,
            [Difference.D26NothingWritten] = 3, // "TRUE", "False", the task without quantity
            [Difference.V8OnlyDateText] = 3,    // "2030/01/15", "Jan 15 2030", " 2030-01-15 "
            [Difference.DateBeyondYear9999] = 1, // 8.64e15
        });
    }

    private static Difference DifferenceOf(JsonElement item)
    {
        if (item.TryGetProperty("error", out var error) && item.GetProperty("effects").GetArrayLength() > 0)
        {
            error.GetString().Should().StartWith("ValidationError: Job validation failed", "only the job's save fails after a write");
            return Difference.D26NothingWritten;
        }

        var body = JsonElement.Parse(item.GetProperty("body").GetString()!);
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("dueDate", out var due) && item.GetProperty("status").GetInt32() == 200)
        {
            if (due.ValueKind == JsonValueKind.String && due.GetString() is "2030/01/15" or "Jan 15 2030" or " 2030-01-15 ")
            {
                return Difference.V8OnlyDateText;
            }

            if (due.ValueKind == JsonValueKind.Number && due.GetDouble() >= 8.64e15)
            {
                return Difference.DateBeyondYear9999;
            }
        }

        return Difference.None;
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
        var expected = JsonNode.Parse(item.GetProperty("json").GetRawText().Normalize(System.Text.NormalizationForm.FormC))!;
        WithoutPassword(expected);
        var actual = JsonNode.Parse(IdPattern().Replace(answer.Text, m => "\"" + (Names.GetValueOrDefault(m.Groups[1].Value) ?? NewId) + "\""))!;
        Sorted(actual).ToJsonString().Should().Be(Sorted(expected).ToJsonString(), because);
        // The job's own keys in the legacy order (the populated budget follows a new budget's order, PLAN.md §10 Phase 3).
        actual.AsObject().Select(p => p.Key).Should().Equal(expected.AsObject().Select(p => p.Key), because);
    }

    private async Task ResetAsync()
    {
        foreach (var name in new[] { "budgets", "tasks", "entities", "jobs", "x-budgets", "x-jobs" })
        {
            await app.Database.DropCollectionAsync(name, Cancellation);
        }

        var budgets = Golden.GetProperty("budgets").EnumerateArray().Select(Document).ToList();
        await Collection("budgets").InsertManyAsync(budgets, cancellationToken: Cancellation);
        await Expected("budgets").InsertManyAsync(budgets.Select(b => b.DeepClone().AsBsonDocument), cancellationToken: Cancellation);
        await Collection("tasks").InsertManyAsync(Golden.GetProperty("tasks").EnumerateArray().Select(Document), cancellationToken: Cancellation);
        await Collection("entities").InsertOneAsync(Document(Golden.GetProperty("entity")), cancellationToken: Cancellation);
        var user = Document(Golden.GetProperty("user"));
        await Collection("users").ReplaceOneAsync(new BsonDocument("_id", user["_id"]), user, new ReplaceOptions { IsUpsert = true }, Cancellation);
    }

    /// <summary>The legacy save's effect, applied to the copy (<c>x-…</c>) of the documents.</summary>
    private async Task ApplyLegacyEffectAsync(JsonElement effect)
    {
        if (effect.TryGetProperty("update", out var update) && effect.TryGetProperty("_id", out var id))
        {
            await Expected("budgets").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(Bind(id.GetProperty("$oid").GetString()!))),
                BsonDocument.Parse(update.GetRawText()), cancellationToken: Cancellation);
            return;
        }

        // §4.3 rule 2: the new app writes NFC (legacy stores a decomposed text as sent).
        var document = Document(JsonElement.Parse(effect.GetProperty("document").GetRawText().Normalize(System.Text.NormalizationForm.FormC)));
        await Expected(effect.GetProperty("insert").GetString()!).InsertOneAsync(document, cancellationToken: Cancellation);
    }

    private async Task DocumentsShouldMatchAsync(string because)
    {
        foreach (var name in new[] { "budgets", "jobs" })
        {
            var actual = await Collection(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            var expected = await Expected(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            actual.Select(Masked).Should().Equal(expected.Select(Masked), $"{name} after {because}");
        }
    }

    /// <summary>
    /// A document's canonical Extended JSON (types and field order); generated ids (the job's and its statistics')
    /// as <c>{{new}}</c>.
    /// </summary>
    private static string Masked(BsonDocument document)
    {
        var copy = document.DeepClone().AsBsonDocument;
        Mask(copy);
        return copy.ToJson(Canonical);

        static void Mask(BsonDocument doc)
        {
            if (doc.TryGetValue("_id", out var id) && (!id.IsObjectId || !Names.ContainsKey(id.ToString()!)))
            {
                doc["_id"] = NewId;
            }

            foreach (var element in doc.Where(e => e.Value.IsBsonDocument))
            {
                Mask(element.Value.AsBsonDocument);
            }
        }
    }

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    private IMongoCollection<BsonDocument> Expected(string name) => app.Database.GetCollection<BsonDocument>("x-" + name);

    private static BsonDocument Document(JsonElement json) =>
        BsonDocument.Parse(Bind(json.GetRawText()).Replace("\"" + NewId + "\"", "\"" + ObjectId.GenerateNewId() + "\"", StringComparison.Ordinal));

    private static string Bind(string text) =>
        NamePattern().Replace(text, m => Ids.TryGetValue(m.Value, out var hex) ? hex : m.Value);

    private static void WithoutPassword(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("password");
                foreach (var (_, value) in obj)
                {
                    WithoutPassword(value);
                }

                break;
            case JsonArray array:
                foreach (var value in array)
                {
                    WithoutPassword(value);
                }

                break;
        }
    }

    private static JsonNode Sorted(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(e => e is null ? null : Sorted(e))]),
        _ => JsonNode.Parse(node.ToJsonString())!,
    };

    [GeneratedRegex(@"\{\{[a-z0-9-]+\}\}")]
    private static partial Regex NamePattern();

    [GeneratedRegex("\"([0-9a-f]{24})\"")]
    private static partial Regex IdPattern();
}

/// <summary>The app (Development) with the golden file's clock (<c>job-golden.json</c> "clock").</summary>
public sealed class AppWithGoldenClock : AppWithMongo
{
    public SettableTime Time { get; } = CreateTime();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));

    private static SettableTime CreateTime()
    {
        var time = new SettableTime();
        time.SetUtcNow(DateTimeOffset.Parse("2026-03-15T10:20:30.123Z", System.Globalization.CultureInfo.InvariantCulture));
        return time;
    }
}
