using System.Net;
using System.Text;
using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>
/// The screenshots for the UAT review with the owner (PLAN.md §8.3 "Visual comparison", §10 Phase 6): every page and
/// settings tab of both apps, loaded from the same seed backup (so the same budget and job ids), at a phone, a laptop
/// and a desktop size, in the default dark theme, side by side in <c>TestResults/uat-gallery/index.html</c>.
/// Colors, texts, icons and content must match; the layout may differ where it fixes a legacy bug (§8.4).
/// Opt-in, not part of <c>tools/verify.sh</c>: <c>DIB4T_UAT_GALLERY=1 dotnet test --project tests/Dib4t.E2E
/// --filter-class '*UatGalleryTests'</c>.
/// </summary>
public sealed class UatGalleryTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly (int Width, int Height)[] Viewports = [(360, 640), (1366, 768), (1920, 1080)];

    private static readonly string[] SettingsTabs = ["Utilizadores", "Preços", "M/Pagamento", "Impressoras", "Base de Dados", "Info"];

    [Fact]
    public async Task Every_page_of_both_apps_side_by_side()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DIB4T_UAT_GALLERY") == "1",
            "set DIB4T_UAT_GALLERY=1 to write the UAT screenshot gallery (TestResults/uat-gallery/)");
        await Harness.LoadSeedAsync(Cancellation);
        var (budgetId, jobId) = Targets(Harness.Seed.ToSnapshot());
        var pages = new List<GalleryPage>
        {
            new("login", "Login", "/", null),
            new("home", "Home", "/landing/home", null),
            new("dashboard", "Dashboard", "/landing/dashboard", null),
            new("budgets", "Orçamentos", "/landing/budgets", null),
            new("budget-details", "Detalhes do orçamento", $"/landing/budgets/{budgetId}", null),
            new("jobs", "Obras", "/landing/job", null),
            new("job-details", "Detalhes da obra", $"/landing/job/{jobId}", null),
            new("job-receivings", "Adjudicações", "/landing/jobReceivings", null),
            new("entities", "Entidades", "/landing/entity", null),
            new("readings", "Leituras", "/landing/leituras", null),
        };
        pages.AddRange(SettingsTabs.Select(tab => new GalleryPage($"settings-{Slug(tab)}", $"Definições · {tab}", "/landing/settings", tab)));

        var output = Path.Combine(Harness.Options.RepoRoot, "TestResults", "uat-gallery");
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        var problems = new List<string>();
        var notes = new List<string>();
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            var page = session.Page;
            foreach (var viewport in Viewports)
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                foreach (var galleryPage in pages)
                {
                    try
                    {
                        await ShowAsync(page, app, galleryPage, notes);
                        await page.ScreenshotAsync(new() { Path = Path.Combine(output, galleryPage.Key, $"{app.Name}-{viewport.Width}x{viewport.Height}.png"), FullPage = true });
                    }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                    {
                        problems.Add($"{app.Name} {galleryPage.Key} {viewport.Width}x{viewport.Height}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "index.html"), Html(pages, problems, notes), Cancellation);
        problems.Should().BeEmpty($"every page is in the gallery ({output}/index.html)");
    }

    /// <summary>Signed in (or out, for the login page) on <paramref name="galleryPage"/>, its data loaded.</summary>
    private async Task ShowAsync(IPage page, IAppUnderTest app, GalleryPage galleryPage, List<string> notes)
    {
        var legacy = app == Harness.Legacy;
        var signedIn = page.Url.StartsWith("http", StringComparison.Ordinal) && await page.EvaluateAsync<bool>("() => !!localStorage.getItem('token')");
        if (galleryPage.Path == "/" && signedIn)
        {
            await page.EvaluateAsync("() => localStorage.removeItem('token')");
        }
        else if (galleryPage.Path != "/" && !signedIn)
        {
            if (legacy)
            {
                await new LegacyLoginPage(page).GotoAsync();
                await new LegacyLoginPage(page).LoginAsync(FixtureUser, FixturePassword);
            }
            else
            {
                await new NewLoginPage(page).GotoAsync();
                await new NewLoginPage(page).LoginAsync(FixtureUser, FixturePassword);
            }
        }

        await page.GotoAsync(galleryPage.Path, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (galleryPage.Tab is { } tab)
        {
            try
            {
                await page.GetByRole(AriaRole.Tab).Filter(new() { HasTextString = tab }).First.ClickAsync(new() { Timeout = 5_000 });
            }
            catch (TimeoutException) when (legacy)
            {
                // LB-12 (docs/layout/legacy-layout-bugs.md): the legacy tabs are cut off or covered by the permanent
                // drawer on phones; the screenshot shows the page as the user gets it.
                notes.Add($"legacy {galleryPage.Key} {page.ViewportSize?.Width}x{page.ViewportSize?.Height}: tab \"{tab}\" cannot be clicked (LB-12)");
            }

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        await page.EvaluateAsync("() => document.fonts.ready");
        await page.Mouse.MoveAsync(0, 0);
        await page.WaitForTimeoutAsync(1_000); // transitions (legacy tab slide 600 ms, drawer, chart animation)
    }

    /// <summary>
    /// The budget of the details page (the one with the longest descriptive, as the legacy layout run picks it: it
    /// wraps) and the job (the first with comments).
    /// </summary>
    private static (string BudgetId, string JobId) Targets(DbSnapshot seed)
    {
        var budget = seed.Collections["budgets"]
            .OrderByDescending(b => b.GetValue("descriptive", "").ToString()!.Length).First();
        var jobs = seed.Collections["jobs"];
        var job = jobs.FirstOrDefault(j => j.GetValue("comments", BsonNull.Value) is BsonString { Value.Length: > 0 }) ?? jobs[0];
        return (budget["_id"].AsObjectId.ToString(), job["_id"].AsObjectId.ToString());
    }

    private static string Slug(string text) =>
        string.Concat(text.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ' || c == '/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

    private static string Html(List<GalleryPage> pages, List<string> problems, List<string> notes)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="pt"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Dib4t UAT gallery</title><style>
            body{font-family:system-ui,sans-serif;margin:16px;background:#1e1e1e;color:#eee}
            h2{margin-top:2.5rem;border-bottom:1px solid #555}h3{font-weight:500;color:#bbb}
            .pair{display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start}
            figure{margin:0}figcaption{font-size:.85rem;color:#aaa;margin-bottom:4px}
            img{max-width:100%;border:1px solid #444}.phone img{max-width:360px}
            nav a{color:#ffb74d;margin-right:.75rem}.problems{color:#ef5350}
            </style></head><body>
            <h1>Dib4t UAT gallery: legacy vs new</h1>
            <p>Same seed backup in both apps, dark theme, 100 % zoom. Colors, texts, icons and content should match; the
            layout may differ where it fixes a legacy bug (PLAN.md §8.4, docs/layout/legacy-layout-bugs.md).</p>
            """);
        html.Append("<nav>");
        foreach (var page in pages)
        {
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<a href=\"#{page.Key}\">{WebUtility.HtmlEncode(page.Title)}</a>");
        }

        html.Append("</nav>");
        if (notes.Count > 0)
        {
            html.Append("<p>Known legacy bugs met:</p><ul>").AppendJoin("", notes.Select(n => $"<li>{WebUtility.HtmlEncode(n)}</li>")).Append("</ul>");
        }

        if (problems.Count > 0)
        {
            html.Append("<ul class=\"problems\">").AppendJoin("", problems.Select(p => $"<li>{WebUtility.HtmlEncode(p)}</li>")).Append("</ul>");
        }

        foreach (var page in pages)
        {
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h2 id=\"{page.Key}\">{WebUtility.HtmlEncode(page.Title)}</h2>");
            foreach (var (width, height) in Viewports)
            {
                var size = $"{width}x{height}";
                html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h3>{size}</h3><div class=\"pair{(width < 600 ? " phone" : "")}\">");
                foreach (var app in new[] { "legacy", "new" })
                {
                    html.Append(System.Globalization.CultureInfo.InvariantCulture,
                        $"<figure><figcaption>{app}</figcaption><a href=\"{page.Key}/{app}-{size}.png\"><img loading=\"lazy\" src=\"{page.Key}/{app}-{size}.png\" alt=\"{app} {WebUtility.HtmlEncode(page.Title)} {size}\"></a></figure>");
                }

                html.Append("</div>");
            }
        }

        return html.Append("</body></html>").ToString();
    }

    private sealed record GalleryPage(string Key, string Title, string Path, string? Tab);
}
