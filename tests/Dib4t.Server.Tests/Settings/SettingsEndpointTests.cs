using System.Text.Json;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Settings;

/// <summary>
/// The settings routes' auth columns (PLAN.md §6: S = <c>canSeeDefinicoes</c>, checked before the body; T = any
/// signed-in user), D2, administrator changes taking effect at once, and the JSON text of the answers.
/// </summary>
public sealed class SettingsEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string AnyId = "0123456789abcdef01234567";

    private readonly HttpClient _admin = app.CreateClient(TestUser.Admin);
    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> SettingsRoutes => new()
    {
        { "POST", "/api/account/create" },
        { "GET", "/api/account/roles" },
        { "GET", "/api/account/users" },
        { "GET", "/api/account/" + AnyId },
        { "PUT", "/api/account/" + AnyId },
        { "DELETE", "/api/account/" + AnyId },
        { "POST", "/api/prices" },
        { "GET", "/api/printer-settings" },
        { "GET", "/api/printer-settings/producao" },
        { "POST", "/api/printer-settings" },
        { "PUT", "/api/printer-settings/abc" },
        { "DELETE", "/api/printer-settings/abc" },
    };

    public static TheoryData<string, string> SignedInRoutes => new()
    {
        { "GET", "/api/prices" },
        { "GET", "/api/entity/getAllPaymentConditions" },
        { "POST", "/api/entity/addPaymentMethod" },
        { "DELETE", "/api/entity/deletePaymentMethod/" + AnyId },
    };

    [Theory]
    [MemberData(nameof(SettingsRoutes))]
    public async Task Settings_routes_need_canSeeDefinicoes_before_anything_else(string method, string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, new HttpMethod(method), path, "{}", null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, new HttpMethod(method), path, "{}", null, Cancellation);

        (limited.Status, limited.Text).Should().Be((401, "Não tem permissão para concluir esta acção."));
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Theory]
    [MemberData(nameof(SignedInRoutes))]
    public async Task Prices_and_payment_methods_are_open_to_every_signed_in_user(string method, string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, new HttpMethod(method), path, "{}", null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, new HttpMethod(method), path, "{}", null, Cancellation);

        limited.Status.Should().NotBe(401);
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Fact]
    public async Task Delete_removes_the_requested_user_not_the_first_one_D2()
    {
        var users = app.Database.GetCollection<BsonDocument>("users");
        var carla = new TestUser(ObjectId.GenerateNewId(), "Carla Sem Área", "carla.semarea@dib4t.local", ["canSeeDashboard"]);
        await carla.SaveAsync(app.Database, Cancellation);
        var firstBefore = (await users.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Cancellation))["_id"];

        var answer = await LegacyHttp.SendAsync(_admin, HttpMethod.Delete, "/api/account/" + carla.Id, null, null, Cancellation);

        (answer.Status, answer.Text).Should().Be((200,
            $$"""{"_id":"{{carla.Id}}","name":"Carla Sem Área","email":"carla.semarea@dib4t.local","roles":["canSeeDashboard"]}"""));
        (await users.CountDocumentsAsync(new BsonDocument("_id", carla.Id), cancellationToken: Cancellation)).Should().Be(0);
        (await users.CountDocumentsAsync(new BsonDocument("_id", firstBefore), cancellationToken: Cancellation)).Should().Be(1);
    }

    /// <summary>The token's user is cached for 30 s (§4.6); an administrator's change drops that user's entry.</summary>
    [Fact]
    public async Task A_role_given_by_an_administrator_works_on_the_next_request()
    {
        var dora = new TestUser(ObjectId.GenerateNewId(), "Dora Leituras", "dora.leituras@dib4t.local", ["canSeeLeituras"]);
        using var client = app.CreateClient(dora);
        (await LegacyHttp.SendAsync(client, HttpMethod.Get, "/api/account/roles", null, null, Cancellation)).Status.Should().Be(401);
        // TestUserHandler re-saves the user before each request: give the role in the document it saves.
        using var promoted = app.CreateClient(dora with { Roles = ["canSeeLeituras", "canSeeDefinicoes"] });

        var edit = await LegacyHttp.SendAsync(_admin, HttpMethod.Put, "/api/account/" + dora.Id,
            """{"name":"Dora Leituras","email":"dora.leituras@dib4t.local","roles":["canSeeLeituras","canSeeDefinicoes"],"fieldOfCompetencies":"Desenvolvimento","password":""}""",
            null, Cancellation);
        var after = await LegacyHttp.SendAsync(promoted, HttpMethod.Get, "/api/account/roles", null, null, Cancellation);

        edit.Status.Should().Be(200);
        after.Status.Should().Be(200, "no wait for the session cache");
    }

    [Fact]
    public async Task Answers_write_numbers_and_text_like_express()
    {
        var update = await LegacyHttp.SendAsync(_admin, HttpMethod.Post, "/api/prices",
            """{"automation":"1e-7","consultancy":0.000001,"development":32.5,"machining":10,"assembly":-0,"margin":35}""", null, Cancellation);

        var prices = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/prices", null, null, Cancellation);
        var method = await LegacyHttp.SendAsync(_admin, HttpMethod.Post, "/api/entity/addPaymentMethod", """{"name":"Transferência a 30 dias"}""", null, Cancellation);

        update.Status.Should().Be(200, update.Text);
        prices.Text.Should().Be("""{"automation":1e-7,"consultancy":0.000001,"development":32.5,"machining":10,"assembly":0,"margin":35}""");
        method.Text.Should().MatchRegex("""^\{"_id":"[0-9a-f]{24}","name":"Transferência a 30 dias","__v":0\}$""");
    }

    [Fact]
    public async Task Without_a_prices_document_get_answers_an_empty_object_and_post_fails_like_legacy()
    {
        var pricesCollection = app.Database.GetCollection<BsonDocument>("prices");
        var saved = await pricesCollection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        await pricesCollection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);
        try
        {
            var get = await LegacyHttp.SendAsync(_admin, HttpMethod.Get, "/api/prices", null, null, Cancellation);
            var post = await LegacyHttp.SendAsync(_admin, HttpMethod.Post, "/api/prices",
                """{"automation":1,"consultancy":1,"development":1,"machining":1,"assembly":1,"margin":10}""", null, Cancellation);

            (get.Status, get.Text).Should().Be((200, "{}"));
            (post.Status, post.Text).Should().Be((500, "Something failed."));
        }
        finally
        {
            if (saved.Count > 0)
            {
                await pricesCollection.InsertManyAsync(saved, cancellationToken: Cancellation);
            }
        }
    }

    [Fact]
    public async Task User_changes_are_logged_in_Auth_without_passwords()
    {
        var body = """{"name":"Eva Registos","email":"eva.registos@dib4t.local","password":"eva-secret-1","roles":["canSeeObras",null]}""";

        var created = await LegacyHttp.SendAsync(_admin, HttpMethod.Post, "/api/account/create", body, null, Cancellation);

        created.Status.Should().Be(200);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString();
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath, r => r.Any(x => x.Message == $"User {id} (eva.registos@dib4t.local) created"), cancellationToken: Cancellation);
        rows.Single(r => r.Message.EndsWith("created", StringComparison.Ordinal) && r.Message.Contains(id!, StringComparison.Ordinal))
            .Category.Should().Be("Auth");
        rows.Should().NotContain(r => (r.Message + r.PropertiesJson + r.Exception).Contains("eva-secret-1", StringComparison.Ordinal));
    }
}
