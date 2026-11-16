using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Budgets;

/// <summary>
/// <c>GET /api/budget/getPdf/:id</c> over HTTP on mongo:9.0.8: the legacy contract (T, 200 base64 text with
/// <c>Content-Type: application/pdf</c> and the legacy disposition), <c>wasPdfDownloaded</c> the only change (as the
/// legacy <c>$set</c>), D16 for what legacy fails on, and the log entries (C11).
/// </summary>
public sealed class BudgetPdfEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_PDF_is_answered_as_base64_and_only_the_flag_is_written()
    {
        var id = await SeedAsync(wasPdfDownloaded: false);
        var before = await StoredAsync(id);

        using var response = await _limited.GetAsync(new Uri("/api/budget/getPdf/" + id, UriKind.Relative), Cancellation);
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        ((int)response.StatusCode).Should().Be(200, "T: no role needed, as legacy");
        response.Content.Headers.ContentType!.ToString().Should().Be("application/pdf");
        response.Content.Headers.NonValidated["Content-Disposition"].ToString().Should().Be("attachment;filename=\"filename.pdf\"", "the legacy header text");
        var pdf = Convert.FromBase64String(text);
        System.Text.Encoding.Latin1.GetString(pdf).Should().StartWith("%PDF-1.3").And.Contain("/Type/Page");
        System.Text.Encoding.Latin1.GetString(pdf).TrimEnd().Should().EndWith("%%EOF");

        var after = await StoredAsync(id);
        after["wasPdfDownloaded"].Should().Be(BsonBoolean.True);
        after.Remove("wasPdfDownloaded");
        before.Remove("wasPdfDownloaded");
        after.Should().BeEquivalentTo(before, "nothing else is written");
        after.Names.Should().Equal(before.Names, "the flag keeps its place in the document");
    }

    [Fact]
    public async Task An_already_downloaded_budget_is_not_written_again()
    {
        var id = await SeedAsync(wasPdfDownloaded: true);
        var before = (await StoredAsync(id)).ToJson();

        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/getpdf/" + id, null, null, Cancellation);

        answer.Status.Should().Be(200, "routes are case-insensitive, as in Express");
        (await StoredAsync(id)).ToJson().Should().Be(before);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("5f1e9a2b3c4d5e6f708100ff")]
    public async Task What_legacy_fails_on_is_the_D16_text(string id)
    {
        await TestUser.Limited.SaveAsync(app.Database, Cancellation);
        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/getPdf/" + id, null, null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, HttpMethod.Get, "/api/budget/getPdf/" + id, null, null, Cancellation);

        (answer.Status, answer.Text).Should().Be((500, "Something failed."));
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Fact]
    public async Task A_PDF_is_logged_with_the_user_and_its_mongo_commands()
    {
        var id = await SeedAsync(wasPdfDownloaded: false);

        var answer = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, "/api/budget/getPdf/" + id, null, null, Cancellation);

        answer.Status.Should().Be(200);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Message.Contains($"Budget {id} (26-0201 R0): PDF made", StringComparison.Ordinal)), cancellationToken: Cancellation);
        var entry = rows.Single(r => r.Message.Contains($"Budget {id} (26-0201 R0): PDF made", StringComparison.Ordinal));
        (entry.Category, entry.Level, entry.UserName).Should().Be((LogCategories.App, "Information", TestUser.Limited.Name));
        var request = rows.Where(r => r.RequestId == entry.RequestId).ToList();
        request.Should().Contain(r => r.Category == LogCategories.Database && r.Message.Contains("update", StringComparison.Ordinal));
        request.Should().Contain(r => r.Category == LogCategories.Http && r.Message.Contains("GET /api/budget/getPdf/{budgetId}", StringComparison.Ordinal));
        rows.Should().NotContain(r => r.Message.Contains("JVBER", StringComparison.Ordinal), "no PDF body in the log");
    }

    private Task<BsonDocument> StoredAsync(ObjectId id) =>
        app.Database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument("_id", id)).SingleAsync(Cancellation);

    private async Task<ObjectId> SeedAsync(bool wasPdfDownloaded)
    {
        await TestUser.Limited.SaveAsync(app.Database, Cancellation);
        var client = ObjectId.GenerateNewId();
        var budget = ObjectId.GenerateNewId();
        await app.Database.GetCollection<BsonDocument>("entities").InsertOneAsync(new BsonDocument
        {
            { "_id", client }, { "name", "Aços do Norte" }, { "fiscalNumber", "500100200" }, { "address", "Rua 1" }, { "zipCode", "4000-001" },
            { "location", "Porto" }, { "paymentCondition", "30 dias" }, { "type", "Cliente" }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        await app.Database.GetCollection<BsonDocument>("budgets").InsertOneAsync(new BsonDocument
        {
            { "_id", budget }, { "revision", 0 }, { "complete", true }, { "wasPdfDownloaded", wasPdfDownloaded }, { "margin", 30 },
            { "daysNeededToDoTheWork", 60 }, { "validity", 30 }, { "hasGenerateJob", false }, { "client", client }, { "descriptive", "Prensa" },
            { "personInCharge", "Eng.ª Maria" }, { "madeBy", TestUser.Limited.Id }, { "budgetNumber", "26-0201" }, { "paymentMethod", "30 dias" },
            { "total", 4143.75 }, { "description", "Fornecimento e montagem." }, { "date", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc) }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        return budget;
    }
}
