using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy budgets page (front-end/src/components/budgets/*, route <c>/landing/budgets</c>).</summary>
public sealed class LegacyBudgetsPage(IPage page) : IBudgetsPage
{
    public ILocator Notification => page.Locator(".Toastify__toast-body");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/budgets");
        await page.Locator("table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task CreateAsync(BudgetValues budget)
    {
        await page.GetByText("Novo orçamento", new() { Exact = true }).ClickAsync(); // a clickable div (newBudgetModdal.jsx)
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("#client").FillAsync(budget.Client);
        await page.GetByRole(AriaRole.Option, new() { Name = budget.Client, Exact = true }).ClickAsync();
        await FillAsync(dialog, budget, "Concluir");
    }

    public async Task CloneAsync(string budgetNumber, BudgetValues budget)
    {
        await SearchAsync(budgetNumber);
        await RowOf(budgetNumber).GetByRole(AriaRole.Button, new() { Name = "more" }).ClickAsync();
        await page.Locator("#long-menu:visible").GetByRole(AriaRole.Menuitem, new() { Name = "Clonar" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasTextString = "Clonar orçamento" });
        await FillAsync(dialog, budget, "Clonar");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    public async Task CreateRevisionAsync(string budgetNumber, double revision)
    {
        await SearchAsync(budgetNumber);
        await RowOf(budgetNumber).GetByRole(AriaRole.Button, new() { Name = "more" }).ClickAsync();
        await page.RunAndWaitForResponseAsync(
            () => page.Locator("#long-menu:visible").GetByRole(AriaRole.Menuitem, new() { Name = "Nova revisão" }).ClickAsync(),
            response => response.Url.Contains("/newrevision/", StringComparison.OrdinalIgnoreCase));
        _ = revision;
    }

    public async Task CreateJobAsync(string budgetNumber, double revision, JobValues job)
    {
        await SearchAsync(budgetNumber);
        _ = revision; // the list row is the latest revision
        await RowOf(budgetNumber).GetByRole(AriaRole.Button, new() { Name = "more" }).ClickAsync();
        await page.Locator("#long-menu:visible").GetByRole(AriaRole.Menuitem, new() { Name = "Criar obra" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasTextString = "Criar obra" });
        await dialog.Locator("#dueDate").FillAsync(job.DueDate);
        await dialog.Locator("#initialJobState").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = job.State, Exact = true }).ClickAsync();
        await dialog.Locator("#orderIdentification").FillAsync(job.OrderIdentification);
        if (job.IsOfficial)
        {
            await dialog.Locator("input[name=isOfficialOrderIdentification]").CheckAsync();
        }

        await page.RunAndWaitForResponseAsync(
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Criar", Exact = true }).ClickAsync(),
            response => response.Url.EndsWith("/api/job/", StringComparison.OrdinalIgnoreCase) || response.Url.EndsWith("/api/job", StringComparison.OrdinalIgnoreCase));
        await page.WaitForURLAsync("**/landing/job"); // legacy goes to the jobs board
    }

    public async Task<IReadOnlyList<string>> RowAsync(string budgetNumber)
    {
        await SearchAsync(budgetNumber);
        return [.. (await RowOf(budgetNumber).Locator("td").AllInnerTextsAsync()).Skip(1).Take(5).Select(t => t.Trim())];
    }

    /// <summary>The toolbar's "Filtrar" field (its label is not linked to an id), Enter = server search.</summary>
    private async Task SearchAsync(string text)
    {
        var filter = page.Locator("input:not([type=checkbox])").First;
        await filter.FillAsync(text);
        await filter.PressAsync("Enter");
        await RowOf(text).First.WaitForAsync();
    }

    private ILocator RowOf(string budgetNumber) =>
        page.Locator("table tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = budgetNumber }) });

    private static async Task FillAsync(ILocator dialog, BudgetValues budget, string submit)
    {
        await dialog.Locator("#descriptive").FillAsync(budget.Descriptive);
        await dialog.Locator("#personInCharge").FillAsync(budget.PersonInCharge);
        await dialog.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
    }
}
