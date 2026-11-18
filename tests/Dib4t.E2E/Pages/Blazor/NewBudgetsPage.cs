using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New budgets page (Dib4t.Client Pages/Landing/Budgets.razor, Components/Budgets/*).</summary>
public sealed class NewBudgetsPage(IPage page) : IBudgetsPage
{
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/budgets");
        await page.Locator(".budgets-table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task CreateAsync(BudgetValues budget)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Novo orçamento" }).ClickAsync();
        await ChooseClientAsync(budget.Client);
        await FillAsync(budget, "Concluir");
    }

    public async Task CloneAsync(string budgetNumber, BudgetValues budget)
    {
        await SearchAsync(budgetNumber);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber} R0", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Clonar" }).ClickAsync();
        await page.Locator("#budget-descriptive").WaitForAsync();
        await FillAsync(budget, "Clonar");
        await page.Locator("#budget-form").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    public async Task CreateRevisionAsync(string budgetNumber, double revision)
    {
        await SearchAsync(budgetNumber);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber} R{revision}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Nova revisão" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber} R{revision + 1}", Exact = true }).WaitForAsync();
    }

    public async Task CreateJobAsync(string budgetNumber, double revision, JobValues job)
    {
        await SearchAsync(budgetNumber);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber} R{revision}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Criar obra" }).ClickAsync();
        await page.Locator("#job-form").WaitForAsync();
        await page.Locator("#job-due-date").FillAsync(job.DueDate);
        await page.Locator("#job-initial-state").SelectOptionAsync(job.State);
        await page.Locator("#job-order").FillAsync(job.OrderIdentification);
        if (job.IsOfficial)
        {
            await page.GetByLabel("Identificação oficial").CheckAsync();
        }

        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Criar", Exact = true }).ClickAsync(),
            response => response.Url.EndsWith("/api/job", StringComparison.OrdinalIgnoreCase));
        await page.Locator("#job-form").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    public async Task<IReadOnlyList<string>> RowAsync(string budgetNumber)
    {
        await SearchAsync(budgetNumber);
        var row = page.Locator(".budgets-table tbody tr").Filter(new() { Has = page.Locator("td.budgets-number", new() { HasTextString = budgetNumber }) });
        return [.. (await row.Locator("td").AllInnerTextsAsync()).Skip(1).Take(5).Select(t => t.Trim())];
    }

    private async Task SearchAsync(string text)
    {
        await page.Locator("#budgets-filter").FillAsync(text);
        await page.Locator("#budgets-filter").PressAsync("Enter");
        await page.Locator(".budgets-table tbody tr td.budgets-number", new() { HasTextString = text }).First.WaitForAsync();
    }

    private async Task ChooseClientAsync(string client)
    {
        await page.Locator("#budget-client").FillAsync(client);
        await page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = client }).First.ClickAsync();
    }

    private async Task FillAsync(BudgetValues budget, string submit)
    {
        await page.Locator("#budget-descriptive").FillAsync(budget.Descriptive);
        await page.Locator("#budget-person").FillAsync(budget.PersonInCharge);
        await page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
    }
}

/// <summary>What the budget list scenarios do, in either UI.</summary>
public interface IBudgetsPage
{
    /// <summary>The notifications (success and error), top right.</summary>
    ILocator Notification { get; }

    Task GotoAsync();

    /// <summary>"Novo orçamento", client, descriptive, person in charge, "Concluir" (author: the signed-in user).</summary>
    Task CreateAsync(BudgetValues budget);

    /// <summary>Searches the number, its menu → "Clonar", the client as offered, descriptive and person retyped, "Clonar".</summary>
    Task CloneAsync(string budgetNumber, BudgetValues budget);

    /// <summary>Searches the number, the menu of its latest revision → "Nova revisão".</summary>
    Task CreateRevisionAsync(string budgetNumber, double revision);

    /// <summary>The number, client, descriptive, person in charge and author of the row, after searching the number.</summary>
    Task<IReadOnlyList<string>> RowAsync(string budgetNumber);

    /// <summary>
    /// Searches the number, the menu of that revision → "Criar obra", the final price as offered, the date, state,
    /// order and flag typed, "Criar"; returns once the job is saved.
    /// </summary>
    Task CreateJobAsync(string budgetNumber, double revision, JobValues job);
}

/// <summary>The values typed in the "Criar obra" dialog (the final price stays the budget's total).</summary>
public sealed record JobValues(string DueDate, string State, string OrderIdentification, bool IsOfficial);

/// <summary>The values chosen and typed in a budget dialog.</summary>
public sealed record BudgetValues(string Client, string Descriptive, string PersonInCharge);
