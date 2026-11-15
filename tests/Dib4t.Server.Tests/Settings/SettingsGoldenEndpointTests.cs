using System.Text.Json;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Settings;

/// <summary>
/// Every body of the legacy users / prices / printers / payment-method golden file (<c>settings-golden.json</c>) sent
/// to the new API: the same status and text, or the same JSON answer (key order and number text included). A
/// failure legacy leaves to Express (refused save, cast error) is <c>500 "Something failed."</c> (D16).
/// </summary>
public sealed class SettingsGoldenEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string FixedId = "5f1e9a2b3c4d5e6f70819299";
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.SettingsGolden));

    /// <summary>Signs the requests; its e-mail is not in any golden body.</summary>
    private static readonly TestUser Admin = TestUser.Admin with { Id = ObjectId.Parse("5f1e9a2b3c4d5e6f708192aa"), Email = "golden.admin@dib4t.local" };

    private readonly HttpClient _client = app.CreateClient(Admin);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<int> CreateUser => Indexes("createUser");

    public static TheoryData<int> EditUser => Indexes("editUser");

    public static TheoryData<int> Prices => Indexes("prices");

    public static TheoryData<int> CreatePrinter => Indexes("createPrinter");

    public static TheoryData<int> EditPrinter => Indexes("editPrinter");

    public static TheoryData<int> CreatePaymentMethod => Indexes("createPaymentMethod");

    [Theory]
    [MemberData(nameof(CreateUser))]
    public async Task Create_user(int index)
    {
        var item = Case("createUser", index);
        await Users().DeleteManyAsync(Builders<BsonDocument>.Filter.Ne("_id", Admin.Id), Cancellation);

        var answer = await SendAsync(HttpMethod.Post, "/api/account/create", item);

        AnswerShouldMatch(item, answer, responseId: true);
    }

    [Theory]
    [MemberData(nameof(EditUser))]
    public async Task Edit_user(int index)
    {
        var item = Case("editUser", index);
        await Users().ReplaceOneAsync(new BsonDocument("_id", ObjectId.Parse(FixedId)), BsonDocument.Parse(item.GetProperty("existing").GetRawText()),
            new ReplaceOptions { IsUpsert = true }, Cancellation);

        var answer = await SendAsync(HttpMethod.Put, "/api/account/" + FixedId, item);

        AnswerShouldMatch(item, answer, responseId: false);
    }

    [Theory]
    [MemberData(nameof(Prices))]
    public async Task Update_prices(int index)
    {
        var item = Case("prices", index);
        var prices = app.Database.GetCollection<BsonDocument>("prices");
        await prices.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);
        await prices.InsertOneAsync(BsonDocument.Parse(Golden.GetProperty("prices").GetProperty("existing").GetRawText()), cancellationToken: Cancellation);

        var answer = await SendAsync(HttpMethod.Post, "/api/prices", item);

        AnswerShouldMatch(item, answer, responseId: false);
    }

    [Theory]
    [MemberData(nameof(CreatePrinter))]
    public async Task Create_printer(int index)
    {
        var item = Case("createPrinter", index);

        var answer = await SendAsync(HttpMethod.Post, "/api/printer-settings", item);

        // Legacy writes and answers the fields in the body's key order; the new app always in the UI's (ip, port, templateName).
        AnswerShouldMatch(item, answer, responseId: true, keyOrder: item.GetProperty("body").GetString()!.StartsWith("{\"ip\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EditPrinter))]
    public async Task Edit_printer(int index)
    {
        var item = Case("editPrinter", index);
        var printers = app.Database.GetCollection<BsonDocument>("printersettings");
        await printers.ReplaceOneAsync(new BsonDocument("_id", ObjectId.Parse(FixedId)),
            BsonDocument.Parse(Golden.GetProperty("editPrinter").GetProperty("existing").GetRawText()), new ReplaceOptions { IsUpsert = true }, Cancellation);

        var answer = await SendAsync(HttpMethod.Put, "/api/printer-settings/" + item.GetProperty("id").GetString(), item);

        AnswerShouldMatch(item, answer, responseId: false);
    }

    [Theory]
    [MemberData(nameof(CreatePaymentMethod))]
    public async Task Add_payment_method(int index)
    {
        var item = Case("createPaymentMethod", index);
        await app.Database.GetCollection<BsonDocument>("paymentmethods").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);

        var answer = await SendAsync(HttpMethod.Post, "/api/entity/addPaymentMethod", item);

        AnswerShouldMatch(item, answer, responseId: true);
    }

    [Fact]
    public async Task Path_ids_answer_like_the_legacy_casts()
    {
        await Users().DeleteManyAsync(Builders<BsonDocument>.Filter.Ne("_id", Admin.Id), Cancellation);
        foreach (var item in Golden.GetProperty("pathIds").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            if (id is "me" or "roles" or "users" || id.Trim() != id)
            {
                continue; // routes of their own in both apps / not a path segment
            }

            var (status, _, text) = await LegacyHttp.SendAsync(_client, HttpMethod.Get, "/api/account/" + id, null, null, Cancellation);

            (status, text).Should().Be(item.GetProperty("cast").GetString() == "ok" ? (404, "not found!") : (500, "Something failed."), id);
        }
    }

    private static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        var list = Golden.GetProperty(section);
        var count = (list.ValueKind == JsonValueKind.Object ? list.GetProperty("cases") : list).GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static JsonElement Case(string section, int index)
    {
        var list = Golden.GetProperty(section);
        return (list.ValueKind == JsonValueKind.Object ? list.GetProperty("cases") : list)[index];
    }

    private static void AnswerShouldMatch(JsonElement item, (int Status, string ContentType, string Text) answer, bool responseId, bool keyOrder = true)
    {
        var body = item.GetProperty("body").GetString();
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
        var actual = JsonElement.Parse(answer.Text);
        if (responseId)
        {
            ObjectId.TryParse(actual.GetProperty("_id").GetString(), out _).Should().BeTrue();
            actual = JsonElement.Parse(answer.Text.Replace(actual.GetProperty("_id").GetString()!, FixedId, StringComparison.Ordinal));
        }

        var expected = item.GetProperty("response");
        if (keyOrder)
        {
            LegacyHttp.Normalize(actual).Should().Be(LegacyHttp.Normalize(expected), body);
        }
        else
        {
            JsonElement.DeepEquals(actual, expected).Should().BeTrue(body);
        }
    }

    private Task<(int Status, string ContentType, string Text)> SendAsync(HttpMethod method, string path, JsonElement item) =>
        LegacyHttp.SendAsync(_client, method, path, item.GetProperty("body").GetString(), null, Cancellation);

    private IMongoCollection<BsonDocument> Users() => app.Database.GetCollection<BsonDocument>("users");
}
