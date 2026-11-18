using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy jobs board (front-end components/job/*): MUI tabs, two "more" buttons per row (state, options).</summary>
public sealed class LegacyJobsPage(IPage page) : IJobsPage
{
    public async Task GotoAsync(string tab)
    {
        await page.GotoAsync("/landing/job");
        await page.GetByRole(AriaRole.Tab).Filter(new() { HasTextString = tab }).ClickAsync();
        await page.Locator("[role=tabpanel]:not([hidden]) table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task ChangeStateAsync(string budgetNumber, string state, string hours)
    {
        await RowOf(budgetNumber).GetByRole(AriaRole.Button, new() { Name = "more" }).Nth(0).ClickAsync();
        await page.Locator("#long-menu:visible").GetByRole(AriaRole.Menuitem, new() { Name = state, Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("input[name=numberOfHoursUsed]").FillAsync(hours);
        await page.RunAndWaitForResponseAsync(
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Concluir", Exact = true }).ClickAsync(),
            response => response.Url.Contains("/api/job/statistics/", StringComparison.OrdinalIgnoreCase));
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    public async Task ChangePriorityAsync(string budgetNumber, string priority)
    {
        await RowOf(budgetNumber).GetByRole(AriaRole.Button, new() { Name = "more" }).Nth(1).ClickAsync();
        await page.RunAndWaitForResponseAsync(
            () => page.Locator("#long-menu:visible").GetByRole(AriaRole.Menuitem, new() { Name = priority, Exact = true }).ClickAsync(),
            response => response.Url.Contains("/api/job/changeJobPriority/", StringComparison.OrdinalIgnoreCase));
    }

    private ILocator RowOf(string budgetNumber) =>
        page.Locator("[role=tabpanel]:not([hidden]) table tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = budgetNumber, Exact = true }) });
}
