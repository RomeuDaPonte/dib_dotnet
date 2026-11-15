using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;

namespace Dib4t.E2E;

/// <summary>
/// The landing shell (PLAN.md §8.3, §8.4, §10 Phase 1 shell step): responsive drawer, theme switch with the legacy
/// colors (compared with the legacy UI), Roboto, and the global error notification.
/// </summary>
public sealed class ShellTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";
    private const string UnexpectedError = "An anexpected error occurred";

    [Theory]
    [InlineData(360, 640, 0, 240)] // phone: overlay drawer, closed
    [InlineData(768, 1024, 0, 240)] // tablet (sm): overlay drawer, closed
    [InlineData(1024, 768, 72, 240)] // md: mini drawer, closed
    [InlineData(1366, 768, 240, 72)] // lg: open, the chevron collapses it to the mini width (legacy permanent drawer)
    public async Task The_drawer_follows_the_screen_width(int width, int height, int initialWidth, int toggledWidth)
    {
        var session = await OpenAsync(Harness.New, new ViewportSize { Width = width, Height = height });
        var shell = await SignInAsync(session.Page);

        await WaitForDrawerWidthAsync(shell, initialWidth);
        if (initialWidth == 240)
        {
            await shell.CloseMenu.ClickAsync();
        }
        else
        {
            (await shell.CloseMenu.IsVisibleAsync()).Should().BeFalse();
            await shell.OpenMenu.ClickAsync();
        }

        await WaitForDrawerWidthAsync(shell, toggledWidth);
        if (toggledWidth == 240)
        {
            await shell.Logout.ClickAsync(new() { Trial = true }); // reachable in the open drawer
            (await shell.OpenMenu.IsVisibleAsync()).Should().BeFalse("legacy hides the menu button while the drawer is open");
        }

        if (initialWidth == 0)
        {
            await session.Page.Locator(".mud-overlay").ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2 } });
            await WaitForDrawerWidthAsync(shell, 0);
            (await shell.Logout.IsVisibleAsync()).Should().BeFalse("a closed overlay drawer is hidden, not only moved off screen");
        }

        session.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task The_theme_switch_shows_the_same_colors_as_the_legacy_app_and_the_login_page_stays_dark()
    {
        var session = await OpenAsync(Harness.New);
        var legacySession = await OpenAsync(Harness.Legacy);
        var shell = await SignInAsync(session.Page);
        var legacyLogin = new LegacyLoginPage(legacySession.Page);
        await legacyLogin.GotoAsync();
        await legacyLogin.LoginAsync(FixtureUser, FixturePassword);
        var legacyShell = new LegacyShell(legacySession.Page);

        var dark = await ColorsAsync(shell.AppBar, shell.Drawer);
        dark.Should().Be(await ColorsAsync(legacyShell.AppBar, legacyShell.Drawer));
        dark.AppBar.Should().Be("rgb(255, 152, 0)", "orange[500] (§8.3)");

        await shell.ThemeSwitch.ClickAsync();
        await legacyShell.ThemeSwitch.ClickAsync();
        await WaitForBackgroundAsync(session.Page, "rgb(250, 250, 250)");

        var light = await ColorsAsync(shell.AppBar, shell.Drawer);
        light.Should().Be(await ColorsAsync(legacyShell.AppBar, legacyShell.Drawer));
        light.AppBar.Should().Be("rgb(3, 169, 244)", "lightBlue[500] (§8.3)");
        (await shell.ThemeSwitch.IsCheckedAsync()).Should().BeTrue();

        await shell.Logout.ClickAsync();
        await new NewLoginPage(session.Page).Email.WaitForAsync();
        (await BackgroundAsync(session.Page)).Should().Be("rgb(48, 48, 48)", "the login page is always dark");

        shell = await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
        (await ColorsAsync(shell.AppBar, shell.Drawer)).Should().Be(dark, "like legacy, the shell opens dark again");
        session.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task The_app_uses_the_bundled_Roboto_font()
    {
        var session = await OpenAsync(Harness.New);
        var shell = await SignInAsync(session.Page);

        var fontFamily = await shell.UserName.EvaluateAsync<string>("e => getComputedStyle(e).fontFamily");
        var loaded = await session.Page.EvaluateAsync<bool>(
            "async () => { await document.fonts.ready; return [...document.fonts].some(f => f.family.replaceAll('\"', '') === 'Roboto' && f.status === 'loaded'); }");

        fontFamily.Should().StartWith("Roboto");
        loaded.Should().BeTrue("the woff2 files are served by the app (no internet needed)");
    }

    [Fact]
    public async Task A_server_error_shows_the_legacy_notification_and_keeps_the_session()
    {
        var session = await OpenAsync(Harness.New);
        await SignInAsync(session.Page);
        await session.Page.RouteAsync("**/api/account/me", route => route.FulfillAsync(new() { Status = 500, Body = "Something failed." }));

        await session.Page.ReloadAsync();
        var shell = new NewShell(session.Page);

        await shell.Notification.Filter(new() { HasText = UnexpectedError }).WaitForAsync();
        (await shell.UserName.InnerTextAsync()).Should().NotBeNullOrEmpty("the name from the token stays");
        (await session.Page.EvaluateAsync<string?>("localStorage.getItem('token')")).Should().NotBeNullOrEmpty("a 5xx does not log out");
    }

    [Fact]
    public async Task A_failed_login_request_shows_one_notification()
    {
        var session = await OpenAsync(Harness.New);
        var login = new NewLoginPage(session.Page);
        await login.GotoAsync();
        await session.Page.RouteAsync("**/api/account/login", route => route.AbortAsync()); // server unreachable

        await login.SubmitAsync(FixtureUser, FixturePassword);

        await login.Notification.First.WaitForAsync();
        await session.Page.WaitForTimeoutAsync(300);
        (await login.Notification.AllInnerTextsAsync()).Should().Equal(UnexpectedError);
    }

    private static async Task<NewShell> SignInAsync(IPage page)
    {
        var login = new NewLoginPage(page);
        await login.GotoAsync();
        return await login.LoginAsync(FixtureUser, FixturePassword);
    }

    /// <summary>Waits out the drawer transition (225 ms), then checks the width in CSS px.</summary>
    private static async Task WaitForDrawerWidthAsync(NewShell shell, int width)
    {
        for (var i = 0; i < 50 && Math.Round(await shell.DrawerWidthAsync()) != width; i++)
        {
            await Task.Delay(100, Cancellation);
        }

        Math.Round(await shell.DrawerWidthAsync()).Should().Be(width);
    }

    private static Task<string> BackgroundAsync(IPage page) => page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

    private static async Task WaitForBackgroundAsync(IPage page, string color) =>
        await page.WaitForFunctionAsync("c => getComputedStyle(document.body).backgroundColor === c", color);

    private static async Task<ShellColors> ColorsAsync(ILocator appBar, ILocator drawer) => new(
        await appBar.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"),
        await appBar.EvaluateAsync<string>("e => getComputedStyle(e).color"),
        await drawer.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"),
        await drawer.EvaluateAsync<string>("e => getComputedStyle(e).color"),
        await appBar.EvaluateAsync<string>("e => getComputedStyle(document.body).backgroundColor"));

    private sealed record ShellColors(string AppBar, string AppBarText, string Drawer, string DrawerText, string Background);
}
