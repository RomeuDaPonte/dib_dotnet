using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New dashboard (Dib4t.Client Pages/Landing/Dashboard.razor: MudChart pie).</summary>
public sealed class NewDashboardPage(IPage page) : IDashboardPage
{
    public async Task<DashboardLoad> GotoAsync()
    {
        var response = await page.RunAndWaitForResponseAsync(() => page.GotoAsync("/landing/dashboard"),
            r => r.Url.Contains("/api/job/getValidTryOnFirstTestCountersByYear/", StringComparison.OrdinalIgnoreCase));
        await page.Locator("[data-testid=dashboard-chart]").WaitForAsync();
        return new DashboardLoad(new Uri(response.Url).AbsolutePath, response.Status, await response.TextAsync());
    }

    public async Task<IReadOnlyList<string>> GotoEmptyAsync()
    {
        await page.GotoAsync("/landing/dashboard");
        // Loaded: the chart's box is there, empty (no height) when there is nothing to draw.
        await page.Locator("[data-testid=dashboard-chart]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        return [.. (await page.Locator("[data-testid=dashboard-chart]").AllInnerTextsAsync()).Select(t => t.Trim()).Where(t => t.Length > 0)];
    }

    public async Task<IReadOnlyList<string>> TextsAsync()
    {
        await page.Locator(".mud-chart-legend-item").First.WaitForAsync();
        return [.. (await page.Locator(".mud-chart-legend-item").AllInnerTextsAsync()).Select(t => t.Trim())];
    }
}

/// <summary>The dashboard in either UI.</summary>
public interface IDashboardPage
{
    /// <summary>Opens the page: the counters request's path and its answer.</summary>
    Task<DashboardLoad> GotoAsync();

    /// <summary>Opens the page on counters of only zeros (answered by the test): the texts the chart area shows once loaded.</summary>
    Task<IReadOnlyList<string>> GotoEmptyAsync();

    /// <summary>The chart's texts (legend; legacy also the slice labels), in page order.</summary>
    Task<IReadOnlyList<string>> TextsAsync();
}

/// <summary>The counters request a dashboard made when it opened.</summary>
public sealed record DashboardLoad(string Path, int Status, string Json);
