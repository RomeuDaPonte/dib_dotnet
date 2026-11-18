using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every case of <c>job-board-golden.json</c> (the legacy job board routes run verbatim in Node: Joi, the
/// repositories' queries and ordering, the job model's methods, Mongoose casts and saves) replayed over HTTP on
/// mongo:9.0.8 from the same stored documents, with the same clock: the same status and text, the same empty answer,
/// or the same JSON (without key order and without the password hash legacy sends in the populated author; list
/// order kept); and the same jobs afterwards, BSON-for-BSON (types and field order), against the legacy save's own
/// <c>$set</c>/<c>$unset</c>/<c>$inc</c> applied to a copy on the same server. A legacy 500 is the D16 text.
/// <c>PATCH state</c> follows the legacy code with D1 fixed (<c>fixed</c>); its <c>legacy</c> answer is checked to be the
/// D1 400 wherever the fix changes it.
/// </summary>
public sealed partial class JobBoardGoldenTests(AppWithGoldenClock app) : IClassFixture<AppWithGoldenClock>
{
    private const string NewId = "{{new}}";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobBoardGolden));

    private static readonly Dictionary<string, string> Ids = Golden.GetProperty("ids").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Names = Ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);

    public static TheoryData<int> Cases
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < Golden.GetProperty("cases").GetArrayLength(); i++)
            {
                data.Add(i);
            }

            return data;
        }
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_legacy_case_gives_the_same_answer_and_documents(int index)
    {
        var item = Golden.GetProperty("cases")[index];
        var expected = item.TryGetProperty("fixed", out var fixedOutcome) ? fixedOutcome : item;
        await ResetAsync();
        foreach (var effect in expected.GetProperty("effects").EnumerateArray())
        {
            await Expected().UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(Bind(effect.GetProperty("_id").GetProperty("$oid").GetString()!))),
                BsonDocument.Parse(WithNewIds(effect.GetProperty("update").GetRawText())), cancellationToken: Cancellation);
        }

        var path = Path(item.GetProperty("path").GetString()!);
        var body = item.GetProperty("body").ValueKind == JsonValueKind.Null ? null : Bind(item.GetProperty("body").GetString()!);
        var answer = await LegacyHttp.SendAsync(_client, new HttpMethod(item.GetProperty("method").GetString()!), path, body, null, Cancellation);

        var because = $"#{index} {item.GetProperty("method").GetString()} {item.GetProperty("path").GetString()} {body}";
        AnswerShouldMatch(expected, answer, because);
        var jobs = await Collection().Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        var legacy = await Expected().Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        jobs.Select(Masked).Should().Equal(legacy.Select(Masked), "jobs after " + because);
    }

    [Fact]
    public void D1_the_legacy_state_route_never_finds_the_job()
    {
        foreach (var item in Golden.GetProperty("cases").EnumerateArray().Where(c => c.TryGetProperty("fixed", out _)))
        {
            var legacy = item.GetProperty("legacy");
            var fixedOutcome = item.GetProperty("fixed");
            if (fixedOutcome.GetProperty("status").GetInt32() == 200)
            {
                (legacy.GetProperty("status").GetInt32(), legacy.GetProperty("text").GetString()).Should().Be((400, "Obra não existe"));
                legacy.GetProperty("effects").GetArrayLength().Should().Be(0);
            }
            else
            {
                legacy.GetRawText().Should().Be(fixedOutcome.GetRawText(), "the fix changes only the lookup");
            }
        }
    }

    private static void AnswerShouldMatch(JsonElement item, (int Status, string ContentType, string Text) answer, string because)
    {
        if (item.TryGetProperty("error", out _))
        {
            (answer.Status, answer.Text).Should().Be((500, "Something failed."), "D16: " + because);
            return;
        }

        answer.Status.Should().Be(item.GetProperty("status").GetInt32(), because);
        if (item.TryGetProperty("empty", out _))
        {
            (answer.ContentType, answer.Text).Should().Be((string.Empty, string.Empty), because);
            return;
        }

        if (item.TryGetProperty("text", out var text))
        {
            answer.ContentType.Should().Be("text/html; charset=utf-8", because);
            answer.Text.Should().Be(text.GetString(), because);
            return;
        }

        answer.ContentType.Should().Be("application/json; charset=utf-8", because);
        var expected = JsonNode.Parse(item.GetProperty("json").GetRawText())!;
        WithoutPassword(expected);
        var actual = JsonNode.Parse(IdPattern().Replace(answer.Text, m => "\"" + (Names.GetValueOrDefault(m.Groups[1].Value) ?? NewId) + "\""))!;
        Sorted(actual).ToJsonString().Should().Be(Sorted(expected).ToJsonString(), because);
    }

    private async Task ResetAsync()
    {
        foreach (var name in new[] { "budgets", "tasks", "entities", "jobs", "x-jobs" })
        {
            await app.Database.DropCollectionAsync(name, Cancellation);
        }

        await app.Database.GetCollection<BsonDocument>("budgets").InsertManyAsync(Golden.GetProperty("budgets").EnumerateArray().Select(Document), cancellationToken: Cancellation);
        await app.Database.GetCollection<BsonDocument>("tasks").InsertManyAsync(Golden.GetProperty("tasks").EnumerateArray().Select(Document), cancellationToken: Cancellation);
        await app.Database.GetCollection<BsonDocument>("entities").InsertOneAsync(Document(Golden.GetProperty("entity")), cancellationToken: Cancellation);
        var user = Document(Golden.GetProperty("user"));
        await app.Database.GetCollection<BsonDocument>("users").ReplaceOneAsync(new BsonDocument("_id", user["_id"]), user,
            new ReplaceOptions { IsUpsert = true }, Cancellation);
        var jobs = Golden.GetProperty("jobs").EnumerateArray().Select(Document).ToList();
        await Collection().InsertManyAsync(jobs, cancellationToken: Cancellation);
        await Expected().InsertManyAsync(jobs.Select(j => j.DeepClone().AsBsonDocument), cancellationToken: Cancellation);
    }

    /// <summary>A document's canonical Extended JSON (types and field order); ids of new sub-documents as <c>{{new}}</c>.</summary>
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

    private IMongoCollection<BsonDocument> Collection() => app.Database.GetCollection<BsonDocument>("jobs");

    private IMongoCollection<BsonDocument> Expected() => app.Database.GetCollection<BsonDocument>("x-jobs");

    private static BsonDocument Document(JsonElement json) => BsonDocument.Parse(Bind(json.GetRawText()));

    private static string Path(string path) => string.Join('/', Bind(path).Split('/').Select(Uri.EscapeDataString));

    private static string WithNewIds(string json) =>
        NewIdPattern().Replace(json, _ => "\"" + ObjectId.GenerateNewId() + "\"");

    [GeneratedRegex("\"\\{\\{new\\}\\}\"")]
    private static partial Regex NewIdPattern();

    private static string Bind(string text) => NamePattern().Replace(text, m => Ids.TryGetValue(m.Value, out var hex) ? hex : m.Value);

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
