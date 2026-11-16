using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New budget details page (Dib4t.Client Pages/Landing/BudgetDetails.razor, Components/Budgets/BudgetDetailsHeader…).</summary>
public sealed class NewBudgetDetailsPage(IPage page) : IBudgetDetailsPage
{
    public IPage Page => page;

    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public ILocator Tasks => page.Locator(".budget-task");

    public ILocator Margin => page.Locator("#budget-margin");

    public ILocator Total => page.Locator("#budget-total");

    public ILocator Days => page.Locator("#budget-days");

    public ILocator Validity => page.Locator("#budget-validity");

    public ILocator Description => page.Locator("#budget-description");

    public async Task GotoAsync(string budgetId)
    {
        await page.GotoAsync("/landing/budgets/" + budgetId);
        await page.Locator(".budget-tasks-total").WaitForAsync(new() { Timeout = 30_000 });
        await Tasks.First.WaitForAsync();
    }

    public ILocator Quantity(int task) => Tasks.Nth(task).Locator("input[id$='-quantity']");

    public ILocator UnitPrice(int task) => Tasks.Nth(task).Locator("input[id$='-unit-price']");

    public Task AddTaskAsync(int task) => Tasks.Nth(task).GetByRole(AriaRole.Button, new() { Name = "Adicionar" }).ClickAsync();

    public Task ToggleViewAsync() => page.GetByRole(AriaRole.Button, new() { Name = "Alternar" }).ClickAsync();

    public Task CompleteAsync() => page.Locator("button.budget-complete").ClickAsync();

    public async Task<IReadOnlyList<string>> TotalsAsync()
    {
        var lines = await page.Locator(".budget-total-line").AllInnerTextsAsync();
        var color = await page.Locator(".budget-effective-margin").EvaluateAsync<string>("e => e.style.color");
        return [.. lines.Select(l => l.Trim()), color];
    }

    public async Task ImportAsync(string budgetNumber)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Importar" }).ClickAsync();
        await page.Locator("#import-number").FillAsync(budgetNumber);
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Importar", Exact = true }).ClickAsync();
    }
}

/// <summary>What the budget details scenarios do, in either UI.</summary>
public interface IBudgetDetailsPage
{
    IPage Page { get; }

    /// <summary>The notifications (success, warning and error), top right.</summary>
    ILocator Notification { get; }

    /// <summary>The task lines, in order.</summary>
    ILocator Tasks { get; }

    ILocator Margin { get; }

    ILocator Total { get; }

    ILocator Days { get; }

    ILocator Validity { get; }

    /// <summary>The description (after "Alternar").</summary>
    ILocator Description { get; }

    Task GotoAsync(string budgetId);

    ILocator Quantity(int task);

    ILocator UnitPrice(int task);

    /// <summary>"Adicionar" of a task line.</summary>
    Task AddTaskAsync(int task);

    /// <summary>"Alternar": tasks ↔ description.</summary>
    Task ToggleViewAsync();

    /// <summary>"Concluir".</summary>
    Task CompleteAsync();

    /// <summary>"Tarefas - X €", "C / Margem - X €", the effective margin "N %", and its color.</summary>
    Task<IReadOnlyList<string>> TotalsAsync();

    /// <summary>"Importar", the number typed (tasks and description ticked, as the dialog opens), "Importar".</summary>
    Task ImportAsync(string budgetNumber);
}
