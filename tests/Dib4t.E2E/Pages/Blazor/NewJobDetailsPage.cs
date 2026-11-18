using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New job details page (Dib4t.Client Pages/Landing/JobDetails.razor, route <c>/landing/job/:id</c>).</summary>
public sealed class NewJobDetailsPage(IPage page) : IJobDetailsPage
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["Desenvolvimento"] = "development",
        ["Componentes"] = "components",
        ["Automação"] = "automation",
        ["Montagem"] = "assembly",
        ["Maquinação"] = "machining",
        ["Materiais"] = "materials",
        ["Outros"] = "others",
        ["Custo não qualidade"] = "custoNaoQualidade",
    };

    public async Task GotoAsync(string jobId)
    {
        await page.GotoAsync("/landing/job/" + jobId);
        await page.Locator(".job-cost-row input").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task RegisterAsync(string row, string value)
    {
        var input = page.Locator($"#job-register-{Types[row]}");
        await input.FillAsync(value);
        await page.RunAndWaitForResponseAsync(() => input.BlurAsync(), r => r.Url.Contains("/api/job/register/", StringComparison.OrdinalIgnoreCase));
    }

    public async Task CommentsAsync(string text)
    {
        var input = page.Locator("#job-comments");
        await input.FillAsync(text);
        await page.RunAndWaitForResponseAsync(() => input.BlurAsync(), r => r.Url.Contains("/api/job/singleProperty/", StringComparison.OrdinalIgnoreCase));
    }

    public Task CheckMachiningOkAsync(int question) =>
        page.RunAndWaitForResponseAsync(
            () => page.Locator("section[aria-label='Controlo de produto'] input[type=checkbox]").Nth((question * 2) + 1).CheckAsync(),
            r => r.Url.Contains("/api/job/updateVerifications/", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<string>> SummaryAsync() =>
        [.. (await page.Locator(".job-summary .job-summary-box").AllInnerTextsAsync()).Select(Normalize)];

    internal static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>The job details page in either UI.</summary>
public interface IJobDetailsPage
{
    Task GotoAsync(string jobId);

    /// <summary>The cost table row's "Registo" input: typed, then left (saved).</summary>
    Task RegisterAsync(string row, string value);

    /// <summary>"Observações": typed, then left (saved).</summary>
    Task CommentsAsync(string text);

    /// <summary>"Controlo de produto": the OK box of question <paramref name="question"/> (0-based).</summary>
    Task CheckMachiningOkAsync(int question);

    /// <summary>The five boxes of the cost summary, as shown (spaces normalized).</summary>
    Task<IReadOnlyList<string>> SummaryAsync();
}
