using System.Text.RegularExpressions;
using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy job details page (front-end/src/components/jobDetais/*, route <c>/landing/job/:id</c>).</summary>
public sealed class LegacyJobDetailsPage(IPage page) : IJobDetailsPage
{
    public async Task GotoAsync(string jobId)
    {
        await page.GotoAsync("/landing/job/" + jobId);
        await page.Locator(".flex-3 input").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    /// <summary>table/*Row.jsx: the row (".flex-3") whose first box is the label.</summary>
    public async Task RegisterAsync(string row, string value)
    {
        var input = page.Locator(".flex-3").Filter(new() { HasTextRegex = new Regex("^\\s*" + Regex.Escape(row)) }).Locator("input");
        await input.FillAsync(value);
        await page.RunAndWaitForResponseAsync(() => input.BlurAsync(), r => r.Url.Contains("/api/job/register/", StringComparison.OrdinalIgnoreCase));
    }

    public async Task CommentsAsync(string text)
    {
        var input = page.Locator("textarea[name=comments]");
        await input.FillAsync(text);
        await page.RunAndWaitForResponseAsync(() => input.PressAsync("Tab"), r => r.Url.Contains("/api/job/singleProperty/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>jobVerifications.jsx: the first ".box-4"; each question row has NOK then OK.</summary>
    public Task CheckMachiningOkAsync(int question) =>
        page.RunAndWaitForResponseAsync(
            () => page.Locator(".box-4").First.Locator(".subContainerQuestionRow input[type=checkbox]").Nth((question * 2) + 1).CheckAsync(),
            r => r.Url.Contains("/api/job/updateVerifications/", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<string>> SummaryAsync() =>
        [.. (await page.Locator(".flex-1 .box-1").AllInnerTextsAsync()).Select(NewJobDetailsPage.Normalize)];
}
