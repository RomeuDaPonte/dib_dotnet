using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy readings page (front-end components/leituras/leiturasInex.jsx: MUI v4 TextField and Dialog).</summary>
public sealed class LegacyReadingsPage(IPage page) : IReadingsPage
{
    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/leituras");
        await page.Locator("input[name=budgetNumber]").WaitForAsync();
    }

    public IPage Page => page;

    public ILocator Field => page.Locator("input[name=budgetNumber]");

    public ILocator State => page.Locator(".MuiDialog-root h1");
}
