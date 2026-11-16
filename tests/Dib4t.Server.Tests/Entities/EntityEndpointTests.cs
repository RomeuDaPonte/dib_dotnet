using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Entities;

/// <summary>
/// The entity routes' auth column (PLAN.md §6: T = any signed-in user, no role, as legacy), the routes that share
/// <c>/api/entity/…</c> with <c>/:id</c>, the answers' JSON text, and the log entries of a change.
/// </summary>
public sealed class EntityEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string AnyId = "0123456789abcdef01234567";

    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/api/entity/getAll" },
        { "GET", "/api/entity/getallclients" },
        { "GET", "/api/entity/getentityTypes" },
        { "GET", "/api/entity/" + AnyId },
        { "POST", "/api/entity" },
        { "PUT", "/api/entity/" + AnyId },
        { "POST", "/api/entity/search" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Entity_routes_are_open_to_every_signed_in_user(string method, string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, new HttpMethod(method), path, "{}", null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, new HttpMethod(method), path, "{}", null, Cancellation);

        limited.Status.Should().NotBe(401, "the limited user has no canSeeEntidades: legacy checks no role here");
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Fact]
    public async Task Literal_routes_win_over_the_id_in_any_case_and_express_json_text()
    {
        await app.Database.GetCollection<BsonDocument>("entities").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Cancellation);
        await app.Database.GetCollection<BsonDocument>("entities").InsertManyAsync(
        [
            new BsonDocument { { "_id", ObjectId.Parse(AnyId) }, { "name", "Fornecedor Só" }, { "paymentCondition", "30 dias" }, { "type", "Fornecedor" }, { "__v", 0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "Aços" }, { "location", "Porto" }, { "paymentCondition", "30 dias" }, { "type", "Cliente" }, { "__v", 0 } },
        ], cancellationToken: Cancellation);

        var all = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/entity/GETALL", null, null, Cancellation);
        var clients = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/entity/getAllClients", null, null, Cancellation);
        var types = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/entity/getentitytypes", null, null, Cancellation);
        var one = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/entity/" + AnyId.ToUpperInvariant(), null, null, Cancellation);
        var getSearch = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/entity/search", null, null, Cancellation);

        all.Text.Should().MatchRegex("""^\[\{"_id":"[0-9a-f]{24}","name":"Aços","location":"Porto","paymentCondition":"30 dias","type":"Cliente","__v":0\},\{"_id":"0123456789abcdef01234567","name":"Fornecedor Só",""");
        clients.Text.Should().NotContain("Fornecedor Só");
        types.Text.Should().Be("""[{"_id":1,"name":"Cliente"},{"_id":2,"name":"Fornecedor"},{"_id":3,"name":"Cliente e Fornecedor"}]""");
        one.Text.Should().Be("""{"_id":"0123456789abcdef01234567","name":"Fornecedor Só","paymentCondition":"30 dias","type":"Fornecedor","__v":0}""");
        (getSearch.Status, getSearch.Text).Should().Be((500, "Something failed."), "GET search is GET /:id with a non-ObjectId, as in Express");
    }

    [Fact]
    public async Task A_change_is_logged_with_its_request_and_user()
    {
        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/entity",
            """{"name":"Registada no Log","fiscalNumber":"","address":"","zipCode":"","location":"","paymentCondition":"30 dias","type":"Cliente"}""",
            null, Cancellation);

        answer.Status.Should().Be(200);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Message.Contains("Registada no Log", StringComparison.Ordinal)), cancellationToken: Cancellation);
        rows.Should().Contain(r => r.Message.EndsWith("created: Registada no Log (Cliente)", StringComparison.Ordinal)
                                   && r.Category == LogCategories.App && r.Level == "Information" && r.UserName == TestUser.Limited.Name);
    }
}
