using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>
/// New landing shell (Dib4t.Client Layout/LandingLayout.razor): user name, menu per role, logout. The responsive
/// MudBlazor shell replaces the placeholder markup in the next step; keep the selectors here only.
/// </summary>
public sealed class NewShell(IPage page)
{
    public ILocator UserName => page.Locator("header h1");

    public ILocator Menu => page.Locator("nav li");

    public ILocator Logout => page.GetByRole(AriaRole.Button, new() { Name = "Sair" });

    public Task<IReadOnlyList<string>> MenuItemsAsync() => Menu.AllInnerTextsAsync();

    /// <summary>Opens <paramref name="path"/> of a signed-in session and waits for the shell.</summary>
    public static async Task<NewShell> OpenAsync(IPage page, string path = "/landing/home")
    {
        await page.GotoAsync(path);
        var shell = new NewShell(page);
        await shell.UserName.WaitForAsync(new() { Timeout = 30_000 });
        return shell;
    }
}
