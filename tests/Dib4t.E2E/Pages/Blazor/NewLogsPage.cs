using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>The Registos tab of the new settings page (Dib4t.Client Components/Settings/LogsTab.razor, PLAN.md §3.3).</summary>
public sealed class NewLogsPage(IPage page)
{
    public ILocator Rows => page.Locator("tr.logs-row");

    public async Task OpenAsync()
    {
        await page.GetByRole(AriaRole.Tab, new() { Name = "Registos" }).ClickAsync();
        await page.Locator("#logs-search").WaitForAsync();
    }

    /// <summary>
    /// Searches <paramref name="text"/> until a row whose message contains <paramref name="expected"/> shows (the log
    /// writer commits every 2 s), and returns that row.
    /// </summary>
    public async Task<ILocator> SearchUntilAsync(string text, string expected)
    {
        await page.Locator("#logs-search").FillAsync(text);
        var row = Rows.Filter(new() { HasText = expected }).First;
        for (var attempt = 0; ; attempt++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Pesquisar", Exact = true }).ClickAsync();
            try
            {
                await row.WaitForAsync(new() { Timeout = 1_000 });
                return row;
            }
            catch (TimeoutException) when (attempt < 15)
            {
            }
        }
    }

    /// <summary>Expands <paramref name="row"/> and clicks its request id: every entry of that request is listed.</summary>
    public async Task<string> ShowRequestOfAsync(ILocator row)
    {
        await row.Locator(".logs-expand").ClickAsync();
        var request = page.Locator(".logs-detail .logs-request").First;
        var requestId = (await request.InnerTextAsync()).Trim();
        await request.ClickAsync();
        await page.WaitForFunctionAsync(
            "id => document.getElementById('logs-request-id').value === id && document.querySelector('.logs-results').getAttribute('aria-busy') === 'false'",
            requestId);
        return requestId;
    }

    public async Task<IReadOnlyList<string>> MessagesAsync() =>
        [.. (await page.Locator("tr.logs-row .logs-message").AllInnerTextsAsync()).Select(t => t.Trim())];
}
