using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.E2E;

/// <summary>
/// Login and logout (PLAN.md §4.6, §10 Phase 1 auth step), on the seed backup's users and their imported passwords.
/// The login form answers are compared with the legacy UI (§11.7).
/// </summary>
public sealed class AuthTests(E2EHarness harness) : E2ETest(harness)
{
    /// <summary>Seed user with six menu roles and null role elements (tools/legacy/dibbak.cs anonymize password).</summary>
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private BsonDocument SeedUser => Harness.Seed.ToSnapshot().Collections["users"].Single(u => u["email"] == FixtureUser);

    [Fact]
    public async Task A_user_of_the_imported_backup_signs_in_and_sees_the_same_menu_as_in_the_legacy_app()
    {
        var newSession = await OpenAsync(Harness.New);
        var legacySession = await OpenAsync(Harness.Legacy);
        var login = new NewLoginPage(newSession.Page);
        var legacyLogin = new LegacyLoginPage(legacySession.Page);

        await login.GotoAsync();
        (await login.Heading.InnerTextAsync()).Should().Be("Sign in");
        var shell = await login.LoginAsync(FixtureUser, FixturePassword);
        await legacyLogin.GotoAsync();
        var legacyShell = await legacyLogin.LoginAsync(FixtureUser, FixturePassword);

        (await shell.UserName.InnerTextAsync()).Should().Be(SeedUser["name"].AsString);
        (await shell.MenuItemsAsync()).Should().Equal(await legacyShell.MenuItemsAsync());
        (await newSession.Page.EvaluateAsync<string?>("localStorage.getItem('token')")).Should().NotBeNullOrEmpty("the login is kept in localStorage (§4.6)");
        newSession.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FixtureUser, "wrong-pass", "Password incorreta!")]
    [InlineData("nobody@dib4t.local", FixturePassword, "Invalid email or password.")]
    public async Task A_rejected_login_shows_the_same_message_as_the_legacy_app(string email, string password, string message)
    {
        var newSession = await OpenAsync(Harness.New);
        var legacySession = await OpenAsync(Harness.Legacy);
        var login = new NewLoginPage(newSession.Page);
        var legacyLogin = new LegacyLoginPage(legacySession.Page);

        await login.GotoAsync();
        await login.SubmitAsync(email, password);
        await legacyLogin.GotoAsync();
        await legacyLogin.SubmitAsync(email, password);

        (await login.Notification.InnerTextAsync()).Should().Be(message);
        (await legacyLogin.Notification.InnerTextAsync()).Should().Be(message);
        newSession.Page.Url.Should().Be(Harness.New.UiBaseUrl.ToString(), "a rejected login stays on the login page");
    }

    [Fact]
    public async Task The_login_survives_closing_the_browser_and_restarting_the_server()
    {
        var profile = Directory.CreateTempSubdirectory("dib4t-e2e-profile-");
        try
        {
            var options = new BrowserTypeLaunchPersistentContextOptions { Headless = !Harness.Options.Headed, BaseURL = Harness.New.UiBaseUrl.ToString() };
            await using (var browser = await Harness.Playwright.Chromium.LaunchPersistentContextAsync(profile.FullName, options))
            {
                await new NewLoginPage(browser.Pages[0]).GotoAsync();
                await new NewLoginPage(browser.Pages[0]).LoginAsync(FixtureUser, FixturePassword);
            }

            // The browser is closed. Reopened on the same profile, the app opens signed in, also from the login URL.
            await using (var browser = await Harness.Playwright.Chromium.LaunchPersistentContextAsync(profile.FullName, options))
            {
                var page = browser.Pages[0];
                await page.GotoAsync("/");
                await page.WaitForURLAsync("**/landing/home", new() { Timeout = 30_000 });
                (await new NewShell(page).UserName.InnerTextAsync()).Should().Be(SeedUser["name"].AsString);

                await Harness.New.RestartAsync();
                var shell = await NewShell.OpenAsync(page);

                (await shell.UserName.InnerTextAsync()).Should().Be(SeedUser["name"].AsString, "same key, so the token is still valid");
            }
        }
        finally
        {
            profile.Delete(recursive: true);
            // A start seeds the legacy administrator when it is missing (startup/seedBd.js, PLAN.md §7.2), as the
            // seed backup's users lack it: back to the exact seed for the other tests.
            await Harness.New.RestoreAsync(Harness.Options.SeedBackup, Cancellation);
        }
    }

    [Fact]
    public async Task Logout_ends_the_session_in_every_tab()
    {
        var session = await OpenAsync(Harness.New);
        await new NewLoginPage(session.Page).GotoAsync();
        var shell = await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
        var otherTab = await session.Context.NewPageAsync();
        await NewShell.OpenAsync(otherTab);

        await shell.Logout.ClickAsync();

        await session.Page.WaitForURLAsync(Harness.New.UiBaseUrl.ToString());
        await otherTab.WaitForURLAsync(Harness.New.UiBaseUrl.ToString());
        await new NewLoginPage(otherTab).Email.WaitForAsync();
        (await otherTab.EvaluateAsync<string?>("localStorage.getItem('token')")).Should().BeNull();

        // Logged out means the landing pages send to the login page again.
        await otherTab.GotoAsync("/landing/home");
        await otherTab.WaitForURLAsync(Harness.New.UiBaseUrl.ToString());
    }

    [Fact]
    public async Task A_removed_role_disappears_without_a_new_login()
    {
        var session = await OpenAsync(Harness.New);
        await new NewLoginPage(session.Page).GotoAsync();
        var shell = await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
        (await shell.MenuItemsAsync()).Should().Contain("Definições");
        (await RestoreStatusAsync(session.Page)).Should().Be(400, "the settings role passes (empty body → 400)");
        var users = Harness.New.Database.GetCollection<BsonDocument>("users");
        try
        {
            await users.UpdateOneAsync(new BsonDocument("_id", SeedUser["_id"]),
                new BsonDocument("$pull", new BsonDocument("roles", "canSeeDefinicoes")), cancellationToken: Cancellation);
            await Task.Delay(NewApp.UserCacheDuration + TimeSpan.FromMilliseconds(200), Cancellation);

            await session.Page.ReloadAsync();
            shell = await NewShell.OpenAsync(session.Page);

            (await shell.MenuItemsAsync()).Should().NotContain("Definições").And.Contain("Orçamentos");
            (await RestoreStatusAsync(session.Page)).Should().Be(401, "the server checks the current roles");
        }
        finally
        {
            await Harness.New.RestoreAsync(Harness.Options.SeedBackup, Cancellation); // back to the seed for the other tests
        }
    }

    /// <summary>An empty restore request with the page's stored token: 400 with the role, 401 without it.</summary>
    private static Task<int> RestoreStatusAsync(IPage page) => page.EvaluateAsync<int>("""
        async () => (await fetch('/api/database/restore', {
            method: 'POST',
            headers: { 'x-auth-token': localStorage.getItem('token'), 'content-type': 'application/octet-stream' },
        })).status
        """);
}
