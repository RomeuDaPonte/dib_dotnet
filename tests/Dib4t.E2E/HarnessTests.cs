using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;

namespace Dib4t.E2E;

/// <summary>
/// The E2E harness itself (PLAN.md §10 Phase 1, §11.7): both apps start, both databases hold the same seed backup,
/// and the page objects of both UIs work. Feature scenarios build on this.
/// </summary>
public sealed class HarnessTests(E2EHarness harness) : E2ETest(harness)
{
    /// <summary>User of the anonymized production fixture (tools/legacy/dibbak.cs anonymize).</summary>
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    [Fact]
    public async Task Both_databases_hold_exactly_the_seed_backup()
    {
        var expected = Harness.Seed.ToSnapshot();

        foreach (var app in Harness.Apps)
        {
            var actual = await app.SnapshotAsync(Cancellation);

            actual.DocumentCount.Should().Be(Harness.Seed.DocumentCount, "{0} holds every seed document", app.Name);
            actual.DiffFrom(expected).Should().BeEmpty("{0} holds the seed documents unchanged (values, BSON types, field order)", app.Name);
        }
    }

    [Fact]
    public async Task Legacy_ui_signs_in_with_a_user_from_the_seed_backup()
    {
        var user = Harness.Seed.ToSnapshot().Collections["users"].Single(u => u["email"] == FixtureUser);
        var session = await OpenAsync(Harness.Legacy);
        var login = new LegacyLoginPage(session.Page);

        await login.GotoAsync();
        var shell = await login.LoginAsync(FixtureUser, FixturePassword);

        (await shell.UserName.InnerTextAsync()).Should().Be(user["name"].AsString);
        (await shell.MenuItemsAsync()).Should().Contain("Orçamentos");
    }

    [Fact]
    public async Task New_app_starts_the_WebAssembly_client()
    {
        var session = await OpenAsync(Harness.New);
        var page = new NewAppPage(session.Page);

        var response = await page.GotoAsync("/");

        response!.Status.Should().Be(200);
        (await page.Heading.InnerTextAsync()).Should().Be("Dib4t");
        session.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task New_app_shows_its_not_found_page_for_an_unknown_route()
    {
        var session = await OpenAsync(Harness.New);
        var page = new NewAppPage(session.Page);

        var response = await page.GotoAsync("/landing/does-not-exist");

        response!.Status.Should().Be(404);
        (await page.Heading.InnerTextAsync()).Should().Be("Página não encontrada");
    }
}
