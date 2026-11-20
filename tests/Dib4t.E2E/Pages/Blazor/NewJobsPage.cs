using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New jobs board (Dib4t.Client Pages/Landing/Jobs.razor, Components/Jobs/*).</summary>
public sealed class NewJobsPage(IPage page) : IJobsPage
{
    public async Task GotoAsync(string tab)
    {
        await page.GotoAsync("/landing/job");
        await page.GetByRole(AriaRole.Tab).Filter(new() { HasTextString = tab }).ClickAsync();
        await page.Locator(".jobs-table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task ChangeStateAsync(string budgetNumber, string state, string hours)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = $"Alterar estado de {budgetNumber}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = state, Exact = true }).ClickAsync();
        await page.Locator("#job-hours").FillAsync(hours);
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Concluir", Exact = true }).ClickAsync(),
            response => response.Url.Contains("/api/job/statistics/", StringComparison.OrdinalIgnoreCase));
        await page.Locator("#job-hours").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    public async Task<PrintOutcome> PrintLabelsAsync(string budgetNumber, bool production, bool machining)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Imprimir", Exact = true }).ClickAsync();
        var dialog = page.Locator(".jobs-print-dialog");
        await dialog.GetByText("Imprimir etiquetas").WaitForAsync();
        foreach (var (label, ticked) in new[] { ("Produção", production), ("Maquinação", machining) })
        {
            await dialog.Locator(".jobs-print-row").Filter(new() { HasTextString = label }).Locator("input[type=checkbox]").SetCheckedAsync(ticked);
        }

        var response = await page.RunAndWaitForResponseAsync(
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Imprimir", Exact = true }).ClickAsync(),
            r => r.Url.Contains("/api/job/print/", StringComparison.OrdinalIgnoreCase));
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var toast = page.Locator(".mud-snackbar").Last;
        return new PrintOutcome(response.Request.PostData, response.Status, await response.TextAsync(), (await toast.InnerTextAsync()).Trim());
    }

    public async Task ChangePriorityAsync(string budgetNumber, string priority)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber}", Exact = true }).ClickAsync();
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Menuitem, new() { Name = priority, Exact = true }).ClickAsync(),
            response => response.Url.Contains("/api/job/changeJobPriority/", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What "Imprimir etiquetas" sent and got: the request body, the answer and the toast shown.</summary>
public sealed record PrintOutcome(string? RequestBody, int Status, string Answer, string Toast);

/// <summary>The jobs board in either UI.</summary>
public interface IJobsPage
{
    /// <summary>Options menu → "Imprimir" → the dialog: the templates (un)ticked, "Imprimir"; the dialog closes.</summary>
    Task<PrintOutcome> PrintLabelsAsync(string budgetNumber, bool production, bool machining);

    /// <summary>Opens the board on the tab whose label contains <paramref name="tab"/>.</summary>
    Task GotoAsync(string tab);

    /// <summary>State menu → <paramref name="state"/> → the statistics dialog: the hours typed, "Concluir".</summary>
    Task ChangeStateAsync(string budgetNumber, string state, string hours);

    /// <summary>Options menu → the priority item.</summary>
    Task ChangePriorityAsync(string budgetNumber, string priority);
}
