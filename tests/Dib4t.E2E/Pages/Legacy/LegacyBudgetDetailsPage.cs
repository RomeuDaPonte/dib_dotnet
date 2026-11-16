using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy budget details page (front-end/src/components/budgetDetails/*, route <c>/landing/budgets/:id</c>).</summary>
public sealed class LegacyBudgetDetailsPage(IPage page) : IBudgetDetailsPage
{
    public IPage Page => page;

    public ILocator Notification => page.Locator(".Toastify__toast-body");

    /// <summary>The task Papers (task.jsx): the ones with a quantity input (its id "Quantidade" repeats per line).</summary>
    public ILocator Tasks => page.Locator(".MuiPaper-root").Filter(new() { Has = page.Locator("[id='Quantidade']") });

    /// <summary>budgetGeneralDetails.jsx: the Paper with "Tarefas -".</summary>
    private ILocator Header => page.Locator(".MuiPaper-root").Filter(new() { Has = page.Locator("h6", new() { HasTextString = "Tarefas -" }) });

    public ILocator Margin => Header.Locator("input[type=number]").Nth(0);

    public ILocator Total => Header.Locator("input[type=number]").Nth(1);

    public ILocator Days => Header.Locator("input[name=daysNeededToDoTheWork]");

    public ILocator Validity => Header.Locator("input[name=validity]");

    public ILocator Description => page.Locator("textarea[name=description]");

    public async Task GotoAsync(string budgetId)
    {
        await page.GotoAsync("/landing/budgets/" + budgetId);
        await Header.WaitForAsync(new() { Timeout = 30_000 });
        await Tasks.First.WaitForAsync();
    }

    public ILocator Quantity(int task) => Tasks.Nth(task).Locator("[id='Quantidade']");

    public ILocator UnitPrice(int task) => Tasks.Nth(task).Locator("[id='Preço unitário']");

    public Task AddTaskAsync(int task) => Tasks.Nth(task).Locator("button[title=Adicionar]").ClickAsync();

    public Task ToggleViewAsync() => page.GetByRole(AriaRole.Button, new() { Name = "Alternar" }).First.ClickAsync();

    public Task CompleteAsync() => page.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();

    public async Task<IReadOnlyList<string>> TotalsAsync()
    {
        var tasks = (await Header.Locator("h6", new() { HasTextString = "Tarefas -" }).InnerTextAsync()).Trim();
        var withMargin = (await Header.Locator("h6", new() { HasTextString = "C / Margem -" }).InnerTextAsync()).Trim();
        var effective = Header.Locator("h6[style*='color']");
        var color = await effective.EvaluateAsync<string>("e => e.style.color");
        return [tasks, withMargin, (await effective.InnerTextAsync()).Trim() + " %", color];
    }

    public async Task ImportAsync(string budgetNumber)
    {
        await Header.Locator("button:has(svg[data-testid=AssignmentReturnedIcon])").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("input[name=budgetNumber]").FillAsync(budgetNumber);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Importar", Exact = true }).ClickAsync();
    }
}
