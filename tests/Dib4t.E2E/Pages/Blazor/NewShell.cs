using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>
/// New landing shell (Dib4t.Client Layout/LandingLayout.razor, PLAN.md §8.3, §8.4): app bar with the user name, the
/// menu button and the theme switch; the drawer with the menu per role and the logout button.
/// </summary>
public sealed class NewShell(IPage page)
{
    public ILocator UserName => page.Locator("header h1");

    public ILocator AppBar => page.Locator("header.mud-appbar");

    public ILocator Drawer => page.Locator("#landing-drawer");

    /// <summary>The menu links (in the drawer), in display order.</summary>
    public ILocator Menu => page.Locator("nav[aria-label=Menu] a");

    /// <summary>"Abrir menu": shown while the drawer is closed (overlay or mini).</summary>
    public ILocator OpenMenu => page.GetByRole(AriaRole.Button, new() { Name = "Abrir menu" });

    /// <summary>The chevron in the drawer header: shown while the drawer is open.</summary>
    public ILocator CloseMenu => page.GetByRole(AriaRole.Button, new() { Name = "Fechar menu" });

    public ILocator ThemeSwitch => page.GetByRole(AriaRole.Switch, new() { Name = "Tema claro" });

    public ILocator Logout => page.GetByRole(AriaRole.Button, new() { Name = "Sair" });

    /// <summary>Error notifications (MudBlazor snackbar, top right).</summary>
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public Task<IReadOnlyList<string>> MenuItemsAsync() => Menu.AllTextContentsAsync();

    /// <summary>The drawer's rendered width in CSS px (0 while an overlay drawer is closed).</summary>
    public Task<double> DrawerWidthAsync() => Drawer.EvaluateAsync<double>(
        "d => d.classList.contains('mud-drawer-temporary') && d.classList.contains('mud-drawer--closed') ? 0 : d.getBoundingClientRect().width");

    /// <summary>Opens <paramref name="path"/> of a signed-in session and waits for the shell.</summary>
    public static async Task<NewShell> OpenAsync(IPage page, string path = "/landing/home")
    {
        await page.GotoAsync(path);
        var shell = new NewShell(page);
        await shell.UserName.WaitForAsync(new() { Timeout = 30_000 });
        return shell;
    }
}
