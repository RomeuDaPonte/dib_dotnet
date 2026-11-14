using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy landing shell (front-end/src/components/landing.jsx + menuListItem.jsx): app bar and drawer menu.</summary>
public sealed class LegacyShell(IPage page)
{
    /// <summary>The app bar title shows the signed-in user's name.</summary>
    public ILocator UserName => page.Locator("header h1");

    public ILocator Menu => page.Locator(".MuiDrawer-paper .MuiListItemText-primary");

    /// <summary>The menu item texts, in order (they depend on the user's roles).</summary>
    public Task<IReadOnlyList<string>> MenuItemsAsync() => Menu.AllInnerTextsAsync();
}
