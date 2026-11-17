using System.Text.Json;
using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Budgets;

/// <summary>
/// The budget list routes over HTTP: the auth column (PLAN.md §6: T, no role, as legacy), Express-like JSON (dates with
/// milliseconds, no password hash in a populated author), the legacy query-string shapes of the search, the
/// supplier's 200 text, the decided D9/D21 answers, and the log entries of a change.
/// </summary>
public sealed class BudgetEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string AnyId = "0123456789abcdef01234567";

    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/api/budget" },
        { "GET", "/api/budget/search?q=ab" },
        { "GET", "/api/budget/getById/" + AnyId },
        { "GET", "/api/budget/revisions/26-0001" },
        { "POST", "/api/budget" },
        { "POST", "/api/budget/clone/" + AnyId },
        { "POST", "/api/budget/newrevision/" + AnyId },
        { "PUT", "/api/budget/" + AnyId },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Budget_routes_are_open_to_every_signed_in_user(string method, string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, new HttpMethod(method), path, "{}", null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, new HttpMethod(method), path, "{}", null, Cancellation);

        limited.Status.Should().NotBe(401, "the limited user has no canSeeOrcamentos: legacy checks no role here");
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Fact]
    public async Task Answers_are_express_json_without_the_password_hash()
    {
        var (client, _) = await SeedAsync();

        var created = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget",
            Body(client, TestUser.Limited.Id, "Prensa"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;
        var one = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/GETBYID/" + id, null, null, Cancellation);
        var list = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/", null, null, Cancellation);
        var revisions = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/revisions/" + JsonElement.Parse(created.Text).GetProperty("budgetNumber").GetString(), null, null, Cancellation);

        created.Status.Should().Be(200);
        created.ContentType.Should().Be("application/json; charset=utf-8");
        created.Text.Should().MatchRegex("""^\{"revision":0,"complete":false,"wasPdfDownloaded":false,"margin":35,"daysNeededToDoTheWork":60,"validity":30,"hasGenerateJob":false,"_id":"[0-9a-f]{24}","client":\{"_id":"[0-9a-f]{24}","name":"Aços do Norte",""");
        created.Text.Should().MatchRegex(""""date":"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z","__v":0\}$"""", "Date#toJSON: always three fraction digits");
        foreach (var text in new[] { created.Text, one.Text, revisions.Text })
        {
            text.Should().Contain("\"madeBy\":{\"roles\":[").And.NotContain("password").And.NotContain("$2a$", "the hash never leaves the server");
        }

        one.Text.Should().Be(created.Text);
        list.Text.Should().MatchRegex("""^\[\{"_id":"[0-9a-f]{24}","budgetNumber":"\d\d-\d{4}","clientName":"Aços do Norte","descriptive":"Prensa","personInCharge":"Eng.ª Maria","madeBy":"Bruno Maquinação","date":"[^"]+\.\d{3}Z","revision":0,"complete":false,"wasPdfDownloaded":false,"hasGenerateJob":false,"total":43\.5,"daysNeededToDoTheWork":60\}""");
        revisions.Text.Should().MatchRegex(""""client":"[0-9a-f]{24}","""", "only the author is populated in the revisions");
    }

    [Fact]
    public async Task Legacy_texts_statuses_and_the_decided_fixes()
    {
        var (client, supplier) = await SeedAsync();

        var supplierAnswer = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget", Body(supplier, TestUser.Limited.Id, "X"), null, Cancellation);
        var slash = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget", Body(client, TestUser.Limited.Id, "Suporte 30/40"), null, Cancellation);
        var badId = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/getById/abc", null, null, Cancellation);
        var missing = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget/newrevision/" + AnyId, null, null, Cancellation);
        var malformed = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget", "{", null, Cancellation);
        var noMadeBy = await LegacyHttp.SendAsync(_limited, HttpMethod.Put, "/api/budget/" + AnyId, """{"descriptive":"x","personInCharge":"y"}""", null, Cancellation);

        (supplierAnswer.Status, supplierAnswer.ContentType, supplierAnswer.Text).Should().Be((200, "text/html; charset=utf-8", "Entidade registada como fornecedor!"));
        (slash.Status, slash.Text).Should().Be((400, "O descritivo não pode conter o carácter \"/\"."), "D21");
        (badId.Status, badId.Text).Should().Be((500, "Something failed."), "a cast error, D16");
        (missing.Status, missing.Text).Should().Be((400, "Orçamento não existe"));
        malformed.Status.Should().Be(400, "express.json() refuses it");
        (noMadeBy.Status, noMadeBy.Text).Should().Be((400, "\"madeById\" is required"), "D7: the edit is validated");
    }

    [Theory]
    [InlineData("q=a", 400, "\"q\" length must be at least 2 characters long")]
    [InlineData("", 400, "\"q\" is required")]
    [InlineData("q=", 400, "\"q\" is not allowed to be empty")]
    [InlineData("q=ab&q=cd", 400, "\"q\" must be a string")]
    [InlineData("q[]=ab", 400, "\"q\" must be a string")]
    [InlineData("q[x]=ab", 400, "\"q\" must be a string")]
    [InlineData("q=ab&extra=1", 400, "\"extra\" is not allowed")]
    [InlineData("Q=ab", 400, "\"q\" is required")]
    [InlineData("q=%28%28", 400, "Error performing search: Regular expression is invalid: missing closing parenthesis")]
    [InlineData("q=a%00b", 400, "Error performing search: value a\0b must not contain null bytes")]
    public async Task Search_reads_the_query_string_like_legacy_express(string query, int status, string text)
    {
        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/search?" + query, null, null, Cancellation);

        (answer.Status, answer.Text).Should().Be((status, text), query);
    }

    [Fact]
    public async Task A_new_budget_is_logged_with_its_request_and_user_and_its_mongo_commands()
    {
        var (client, _) = await SeedAsync();

        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Post, "/api/budget", Body(client, TestUser.Limited.Id, "Registado no Log"), null, Cancellation);

        answer.Status.Should().Be(200);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Category == LogCategories.App && x.Message.Contains("Registado no Log", StringComparison.Ordinal)), cancellationToken: Cancellation);
        var created = rows.Single(r => r.Category == LogCategories.App && r.Message.Contains("Registado no Log", StringComparison.Ordinal));
        (created.Category, created.Level, created.UserName).Should().Be((LogCategories.App, "Information", TestUser.Limited.Name));
        var request = rows.Where(r => r.RequestId == created.RequestId).ToList();
        request.Should().Contain(r => r.Category == LogCategories.Database && r.Message.Contains("insert", StringComparison.Ordinal) && r.Message.Contains(".budgets", StringComparison.Ordinal));
        request.Should().Contain(r => r.Category == LogCategories.Database && r.Message.Contains(".tasks", StringComparison.Ordinal), "the default task (D6)");
        request.Should().Contain(r => r.Category == LogCategories.Http && r.Message.Contains("POST /api/budget", StringComparison.Ordinal));
        rows.Should().NotContain(r => r.Message.Contains("$2a$", StringComparison.Ordinal));
    }

    private static string Body(ObjectId client, ObjectId madeBy, string descriptive) =>
        JsonSerializer.Serialize(new { clientId = client.ToString(), madeById = madeBy.ToString(), descriptive, personInCharge = "Eng.ª Maria" });

    private async Task<(ObjectId Client, ObjectId Supplier)> SeedAsync()
    {
        await TestUser.Limited.SaveAsync(app.Database, Cancellation);
        var client = ObjectId.GenerateNewId();
        var supplier = ObjectId.GenerateNewId();
        await app.Database.GetCollection<BsonDocument>("entities").InsertManyAsync(
        [
            new BsonDocument { { "_id", client }, { "name", "Aços do Norte" }, { "paymentCondition", "30 dias" }, { "type", "Cliente" }, { "__v", 0 } },
            new BsonDocument { { "_id", supplier }, { "name", "Só Fornece" }, { "paymentCondition", "30 dias" }, { "type", "Fornecedor" }, { "__v", 0 } },
        ], cancellationToken: Cancellation);
        var prices = app.Database.GetCollection<BsonDocument>("prices");
        await prices.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("automation", 32.5)), cancellationToken: Cancellation);
        return (client, supplier);
    }
}
