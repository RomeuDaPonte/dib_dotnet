using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Entities;

/// <summary>
/// Every body of the legacy entity golden file (<c>entity-golden.json</c>) sent to the new API: the same status and
/// text, or the same JSON answer (key order included). A failure legacy leaves to Express (refused save, cast error,
/// invalid search pattern) is <c>500 "Something failed."</c> (D16). Known differences, decided here (ADR-0004: no
/// other API consumers): the fields of a new entity are written and answered in the UI's order (legacy: the body's);
/// a <c>null</c> text is answered like a missing one (legacy: <c>null</c>; neither UI sends one); names are stored
/// and answered NFC (§4.3 rule 2).
/// </summary>
public sealed class EntityGoldenEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string FixedId = "5f1e9a2b3c4d5e6f70819299";
    private static readonly string[] UiOrder = ["name", "fiscalNumber", "address", "zipCode", "location", "paymentCondition", "type"];
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.EntityGolden));

    private readonly HttpClient _client = app.CreateClient(TestUser.Limited);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<int> CreateCases => Indexes("createEntity");

    public static TheoryData<int> EditCases => Indexes("editEntity");

    public static TheoryData<int> SearchCases => Indexes("search");

    [Theory]
    [MemberData(nameof(CreateCases))]
    public async Task Create_entity(int index)
    {
        var item = Golden.GetProperty("createEntity")[index];
        await Entities().DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);

        var answer = await SendAsync(HttpMethod.Post, "/api/entity", item);

        AnswerShouldMatch(item, answer, newId: true);
    }

    [Fact]
    public async Task Create_refuses_a_name_already_registered_before_the_casts()
    {
        await Entities().DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);
        var body = Golden.GetProperty("createEntity")[0].GetProperty("body").GetString()!;
        (await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/entity", body, null, Cancellation)).Status.Should().Be(200);

        var again = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/entity",
            body.Replace("\"fiscalNumber\":\"509999990\"", "\"fiscalNumber\":{}", StringComparison.Ordinal), null, Cancellation);

        (again.Status, again.Text).Should().Be((400, "Nome de entidade já registado"));
    }

    [Theory]
    [MemberData(nameof(EditCases))]
    public async Task Edit_entity(int index)
    {
        var item = Golden.GetProperty("editEntity")[index];
        var existing = item.GetProperty("existing");
        if (existing.TryGetProperty("fiscalNumber", out var stored) && stored.ValueKind == JsonValueKind.Object)
        {
            return; // a stored number: never written by either app (Mongoose casts to string), not in production
        }

        await Entities().ReplaceOneAsync(new BsonDocument("_id", ObjectId.Parse(FixedId)), BsonDocument.Parse(existing.GetRawText()),
            new ReplaceOptions { IsUpsert = true }, Cancellation);

        var answer = await SendAsync(HttpMethod.Put, "/api/entity/" + FixedId, item);

        AnswerShouldMatch(item, answer, newId: false);
    }

    [Theory]
    [MemberData(nameof(SearchCases))]
    public async Task Search_entities(int index)
    {
        var item = Golden.GetProperty("search")[index];
        var body = item.GetProperty("body").GetString()!;

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/entity/search", body, null, Cancellation);

        // "^\k": JavaScript accepts it, MongoDB's PCRE does not, so the legacy query fails as well.
        var fails = item.GetProperty("outcome").GetString() == "500" || body.Contains("\"\\\\k\"", StringComparison.Ordinal);
        (answer.Status, fails ? answer.Text : "[...]").Should().Be(fails ? (500, "Something failed.") : (200, "[...]"), body);
    }

    [Fact]
    public async Task Path_ids_answer_like_the_legacy_casts()
    {
        await Entities().DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);
        foreach (var item in Golden.GetProperty("pathIds").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            if (id == "getAll")
            {
                continue; // its own route in both apps
            }

            var (status, _, text) = await LegacyHttp.SendAsync(_client, HttpMethod.Get, "/api/entity/" + id, null, null, Cancellation);

            (status, text).Should().Be(item.GetProperty("cast").GetString() == "ok" ? (400, "Entidade não encontrada") : (500, "Something failed."), id);
        }
    }

    [Fact]
    public async Task Stored_entities_are_answered_as_stored()
    {
        foreach (var item in Golden.GetProperty("read").EnumerateArray())
        {
            var stored = BsonDocument.Parse(item.GetProperty("stored").GetRawText());
            if (stored.GetValue("fiscalNumber", "").IsNumeric || stored.Contains("extra") || !stored.Contains("__v") || stored.ElementCount > 0 && stored.GetElement(0).Name != "_id")
            {
                continue; // shapes neither app writes: a number, an unknown field, no __v, another field order (none in production)
            }

            await Entities().ReplaceOneAsync(new BsonDocument("_id", ObjectId.Parse(FixedId)), stored, new ReplaceOptions { IsUpsert = true }, Cancellation);

            var one = await LegacyHttp.SendAsync(_client, HttpMethod.Get, "/api/entity/" + FixedId, null, null, Cancellation);

            one.Status.Should().Be(200);
            LegacyHttp.Normalize(one.Text).Should().Be(LegacyHttp.Normalize(WithoutNulls(item.GetProperty("response")).ToJsonString()));
        }
    }

    private static void AnswerShouldMatch(JsonElement item, (int Status, string ContentType, string Text) answer, bool newId)
    {
        var body = item.GetProperty("body").GetString()!;
        if (item.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            (answer.Status, answer.ContentType, answer.Text).Should().Be((400, "text/html; charset=utf-8", message.GetString()!), body);
            return;
        }

        if (item.GetProperty("outcome").GetString() == "500")
        {
            (answer.Status, answer.Text).Should().Be((500, "Something failed."), body);
            return;
        }

        (answer.Status, answer.ContentType).Should().Be((200, "application/json; charset=utf-8"), body);
        var actual = JsonNode.Parse(answer.Text)!.AsObject();
        if (newId)
        {
            ObjectId.TryParse(actual["_id"]!.GetValue<string>(), out _).Should().BeTrue();
            actual["_id"] = FixedId; // the golden answer carries the id the script gave the new document
        }

        var expected = WithoutNulls(item.GetProperty("response"));
        foreach (var (key, value) in expected.ToList())
        {
            if (value is JsonValue text && text.TryGetValue<string>(out var s))
            {
                expected[key] = s.Normalize(NormalizationForm.FormC);
            }
        }

        if (InUiOrder(body))
        {
            LegacyHttp.Normalize(actual.ToJsonString()).Should().Be(LegacyHttp.Normalize(expected.ToJsonString()), body);
        }
        else
        {
            JsonNode.DeepEquals(actual, expected).Should().BeTrue(body);
        }
    }

    private static JsonObject WithoutNulls(JsonElement response)
    {
        var json = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var key in json.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            json.Remove(key);
        }

        return json;
    }

    private static bool InUiOrder(string body)
    {
        var keys = JsonElement.Parse(body).EnumerateObject().Select(p => Array.IndexOf(UiOrder, p.Name)).ToList();
        return keys.All(k => k >= 0) && keys.SequenceEqual(keys.Order()) && keys.Distinct().Count() == keys.Count;
    }

    private static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Golden.GetProperty(section).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    private Task<(int Status, string ContentType, string Text)> SendAsync(HttpMethod method, string path, JsonElement item) =>
        LegacyHttp.SendAsync(_client, method, path, item.GetProperty("body").GetString(), null, Cancellation);

    private IMongoCollection<BsonDocument> Entities() => app.Database.GetCollection<BsonDocument>("entities");
}
