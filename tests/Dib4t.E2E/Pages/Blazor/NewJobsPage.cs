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

    public async Task ChangePriorityAsync(string budgetNumber, string priority)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = $"Opções de {budgetNumber}", Exact = true }).ClickAsync();
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Menuitem, new() { Name = priority, Exact = true }).ClickAsync(),
            response => response.Url.Contains("/api/job/changeJobPriority/", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The jobs board in either UI.</summary>
public interface IJobsPage
{
    /// <summary>Opens the board on the tab whose label contains <paramref name="tab"/>.</summary>
    Task GotoAsync(string tab);

    /// <summary>State menu → <paramref name="state"/> → the statistics dialog: the hours typed, "Concluir".</summary>
    Task ChangeStateAsync(string budgetNumber, string state, string hours);

    /// <summary>Options menu → the priority item.</summary>
    Task ChangePriorityAsync(string budgetNumber, string priority);
}
