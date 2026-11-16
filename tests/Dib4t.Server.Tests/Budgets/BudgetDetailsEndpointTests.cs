using System.Text.Json;
using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Budgets;

/// <summary>
/// The budget details routes over HTTP (the legacy contract itself is <see cref="BudgetDetailsGoldenTests"/>): the auth
/// column (PLAN.md §6: T, no role, as legacy), the literal task routes before the id routes in any case, and the log
/// entries of a change (C11: an App event with the user, its Mongo commands and HTTP entry under one request id).
/// </summary>
public sealed class BudgetDetailsEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private const string AnyId = "0123456789abcdef01234567";

    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "PUT", "/api/budget/patchProperty/" + AnyId },
        { "PUT", "/api/budget/import/" + AnyId },
        { "GET", "/api/task/getTaskTypes" },
        { "GET", "/api/task/getBudgetTasks/" + AnyId },
        { "POST", "/api/task/createDefaultTask/" + AnyId },
        { "POST", "/api/task" },
        { "PUT", $"/api/task/{AnyId}/{AnyId}" },
        { "DELETE", $"/api/task/{AnyId}/{AnyId}" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Details_routes_are_open_to_every_signed_in_user(string method, string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, new HttpMethod(method), path, "{}", null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, new HttpMethod(method), path, "{}", null, Cancellation);

        limited.Status.Should().NotBe(401, "the limited user has no canSeeOrcamentos: legacy checks no role here");
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Fact]
    public async Task Task_types_are_the_legacy_list_in_any_case()
    {
        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/task/gettasktypes", null, null, Cancellation);

        (answer.Status, answer.Text).Should().Be((200, """["automation","consultancy","development","machining","assembly","others","components"]"""),
            "the legacy UI calls /task/gettasktypes");
    }

    [Fact]
    public async Task A_task_edit_and_a_patch_are_logged_with_the_user_and_their_mongo_commands()
    {
        var (budgetId, taskId) = await SeedAsync();

        var edit = await LegacyHttp.SendAsync(_limited, HttpMethod.Put, $"/api/task/{taskId}/{budgetId}",
            """{"taskType":"development","description":"Logged","quantity":"2","unitPrice":30}""", null, Cancellation);
        var patch = await LegacyHttp.SendAsync(_limited, HttpMethod.Put, "/api/budget/patchProperty/" + budgetId,
            """{"propertyName":"total","value":"78"}""", null, Cancellation);

        (edit.Status, patch.Status).Should().Be((200, 200));
        JsonElement.Parse(edit.Text).GetProperty("quantity").GetDouble().Should().Be(2);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Message.Contains("total changed", StringComparison.Ordinal)) && r.Any(x => x.Message.Contains($"Task {taskId}", StringComparison.Ordinal)),
            cancellationToken: Cancellation);
        foreach (var (text, route) in new[] { ($"Task {taskId}", "PUT /api/task/{taskId}/{budgetId}"), ("total changed", "PUT /api/budget/patchProperty/{budgetId}") })
        {
            var entry = rows.Single(r => r.Message.Contains(text, StringComparison.Ordinal));
            (entry.Category, entry.Level, entry.UserName).Should().Be((LogCategories.App, "Information", TestUser.Limited.Name));
            var request = rows.Where(r => r.RequestId == entry.RequestId).ToList();
            request.Should().Contain(r => r.Category == LogCategories.Database && r.Message.Contains("update", StringComparison.Ordinal));
            request.Should().Contain(r => r.Category == LogCategories.Http && r.Message.Contains(route, StringComparison.Ordinal));
        }

        rows.Should().NotContain(r => r.Message.Contains("Logged", StringComparison.Ordinal), "no body content in the log");
    }

    private async Task<(ObjectId Budget, ObjectId Task)> SeedAsync()
    {
        await TestUser.Limited.SaveAsync(app.Database, Cancellation);
        var client = ObjectId.GenerateNewId();
        var budget = ObjectId.GenerateNewId();
        var task = ObjectId.GenerateNewId();
        await app.Database.GetCollection<BsonDocument>("entities").InsertOneAsync(
            new BsonDocument { { "_id", client }, { "name", "Aços do Norte" }, { "paymentCondition", "30 dias" }, { "type", "Cliente" }, { "__v", 0 } },
            cancellationToken: Cancellation);
        await app.Database.GetCollection<BsonDocument>("budgets").InsertOneAsync(new BsonDocument
        {
            { "_id", budget }, { "revision", 0 }, { "complete", true }, { "wasPdfDownloaded", true }, { "margin", 30 }, { "daysNeededToDoTheWork", 60 },
            { "validity", 30 }, { "hasGenerateJob", false }, { "client", client }, { "descriptive", "Prensa" }, { "personInCharge", "Eng.ª Maria" },
            { "madeBy", TestUser.Limited.Id }, { "budgetNumber", "26-0101" }, { "paymentMethod", "30 dias" }, { "total", 43.5 },
            { "date", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc) }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        await app.Database.GetCollection<BsonDocument>("tasks").InsertOneAsync(new BsonDocument
        {
            { "_id", task }, { "budgetId", budget }, { "taskType", "automation" }, { "description", "" }, { "unitPrice", 32.5 }, { "quantity", 1 }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        return (budget, task);
    }
}
