using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy dashboard (front-end components/dashboard/dashboardIndex.jsx: react-google-charts PieChart, SVG).</summary>
public sealed class LegacyDashboardPage(IPage page) : IDashboardPage
{
    public async Task<DashboardLoad> GotoAsync()
    {
        var response = await page.RunAndWaitForResponseAsync(() => page.GotoAsync("/landing/dashboard"),
            r => r.Url.Contains("/api/job/getValidTryOnFirstTestCountersByYear/", StringComparison.OrdinalIgnoreCase));
        await page.Locator("svg text").First.WaitForAsync(new() { Timeout = 30_000 });
        return new DashboardLoad(new Uri(response.Url).AbsolutePath, response.Status, await response.TextAsync());
    }

    public async Task<IReadOnlyList<string>> GotoEmptyAsync()
    {
        await page.RunAndWaitForResponseAsync(() => page.GotoAsync("/landing/dashboard"),
            r => r.Url.Contains("/api/job/getValidTryOnFirstTestCountersByYear/", StringComparison.OrdinalIgnoreCase));
        // Google Charts draws inside a div of the react-google-charts wrapper once its loader has run.
        await page.Locator("div[id^='reactgooglegraph']").First.WaitForAsync(new() { Timeout = 30_000 });
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        // The drawn texts (the wrapper also holds Google's off-screen data table for screen readers).
        return [.. (await page.Locator("div[id^='reactgooglegraph'] svg text").AllTextContentsAsync()).Select(t => t.Trim()).Where(t => t.Length > 0)];
    }

    public async Task<IReadOnlyList<string>> TextsAsync() =>
        [.. (await page.Locator("svg text").AllTextContentsAsync()).Select(t => t.Trim()).Where(t => t.Length > 0)];
}
