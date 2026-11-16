using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.E2E;

/// <summary>
/// Registos (PLAN.md §3.3, §10 Phase 2): a change made in the UI is found by text and, through its request id, with
/// every entry of that request (HTTP line, Mongo commands, use-case event). A user without <c>canSeeDefinicoes</c>
/// gets neither the tab nor the API. (The plan's "create a budget" variant waits for Phase 3; a printer is the change
/// with an HTTP request, a Mongo command and a use-case event today.)
/// </summary>
public sealed class LogsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";
    private const string PrinterIp = "10.77.0.42";

    [Fact]
    public async Task A_change_is_found_in_Registos_by_text_and_by_request_id()
    {
        try
        {
            var session = await OpenAsync(Harness.New);
            await new NewLoginPage(session.Page).GotoAsync();
            await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            var settings = new NewSettingsPage(session.Page);
            await settings.GotoAsync();
            await settings.AddPrinterAsync(PrinterIp, "9100", "Produção");
            await settings.Notification.First.WaitForAsync();

            var logs = new NewLogsPage(session.Page);
            await logs.OpenAsync();
            var row = await logs.SearchUntilAsync(PrinterIp, $"added: {PrinterIp}:9100");
            (await row.InnerTextAsync()).Should().Contain("Printer");

            var requestId = await logs.ShowRequestOfAsync(row);

            requestId.Should().MatchRegex("^[0-9a-f]{32}$");
            var messages = await logs.MessagesAsync();
            messages.Should().Contain(m => m.StartsWith("HTTP POST /api/printer-settings responded 200 in ", StringComparison.Ordinal));
            messages.Should().Contain(m => m.StartsWith("Mongo insert dib4t.printersettings ", StringComparison.Ordinal));
            messages.Should().Contain(m => m.Contains($"added: {PrinterIp}:9100", StringComparison.Ordinal));
            (await logs.Rows.AllInnerTextsAsync()).Should().OnlyContain(r => r.Contains(SeedUserName, StringComparison.Ordinal), "every entry has the user");
            session.Errors.Should().BeEmpty();
        }
        finally
        {
            await Harness.New.RestoreAsync(Harness.Options.SeedBackup, Cancellation);
        }
    }

    [Fact]
    public async Task Without_canSeeDefinicoes_there_is_no_tab_and_the_API_answers_401()
    {
        var users = Harness.New.Database.GetCollection<BsonDocument>("users");
        try
        {
            await users.UpdateOneAsync(new BsonDocument("email", FixtureUser),
                new BsonDocument("$pull", new BsonDocument("roles", "canSeeDefinicoes")), cancellationToken: Cancellation);
            await Task.Delay(NewApp.UserCacheDuration + TimeSpan.FromMilliseconds(200), Cancellation);
            var session = await OpenAsync(Harness.New);
            await new NewLoginPage(session.Page).GotoAsync();
            await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);

            await session.Page.GotoAsync("/landing/settings");
            await session.Page.Locator(".settings-denied").WaitForAsync();

            (await session.Page.GetByRole(Microsoft.Playwright.AriaRole.Tab, new() { Name = "Registos" }).CountAsync()).Should().Be(0);
            foreach (var path in new[] { "/api/logs", "/api/logs/facets" })
            {
                (await session.Page.EvaluateAsync<int>(
                    "async path => (await fetch(path, { headers: { 'x-auth-token': localStorage.getItem('token') } })).status", path))
                    .Should().Be(401, path);
            }
        }
        finally
        {
            await Harness.New.RestoreAsync(Harness.Options.SeedBackup, Cancellation);
        }
    }

    private string SeedUserName => Harness.Seed.ToSnapshot().Collections["users"].Single(u => u["email"] == FixtureUser)["name"].AsString;
}
