using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New readings page (Dib4t.Client Pages/Landing/Readings.razor: MudTextField and MudDialog).</summary>
public sealed class NewReadingsPage(IPage page) : IReadingsPage
{
    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/leituras");
        await page.Locator("#readings-budget-number").WaitForAsync();
    }

    public IPage Page => page;

    public ILocator Field => page.Locator("#readings-budget-number");

    public ILocator State => page.Locator("[data-testid=readings-state]");
}

/// <summary>The readings page in either UI.</summary>
public interface IReadingsPage
{
    /// <summary>Opens the page; the field is there (and focused: both autofocus it).</summary>
    Task GotoAsync();

    IPage Page { get; }

    /// <summary>The "N Orçamento" input.</summary>
    ILocator Field { get; }

    /// <summary>The dialog's state heading (gone once the dialog is closed).</summary>
    ILocator State { get; }
}
