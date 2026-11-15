using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Dib4t.E2E.Harness;
using Dib4t.E2E.Layout;
using Microsoft.Playwright;

namespace Dib4t.E2E;

/// <summary>
/// The §8.4 layout matrix on every page of the new app (PLAN.md §11.8, ADR-0002): viewports 360×640 … 2560×1440 ×
/// browser zoom 100/150/200 % × root font size 16/20/24 px. Each case asserts no horizontal page scroll, no key control
/// outside the viewport, no overlapping controls or text, nothing cut off, the primary actions visible and clickable,
/// and no serious/critical axe-core violation. Pages are added here with each slice.
/// Screenshots and a report go to <c>TestResults/layout-matrix/</c> (review them next to the legacy "before" run).
/// </summary>
public sealed class LayoutMatrixTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly (int Width, int Height)[] Viewports = [(360, 640), (768, 1024), (1366, 768), (1920, 1080), (2560, 1440)];
    private static readonly double[] Zooms = [1, 1.5, 2];
    private static readonly int[] Fonts = [16, 20, 24];

    private static readonly LayoutPage[] Pages =
    [
        new("login", "/", SignedIn: false, Ready: "#email", page =>
        [
            ("email", page.Locator("#email")),
            ("password", page.Locator("#password")),
            ("Submit", page.GetByRole(AriaRole.Button, new() { Name = "Submit" })),
        ]),
        new("home", "/landing/home", SignedIn: true, Ready: "header h1", page =>
        [
            ("Tema claro", page.GetByRole(AriaRole.Switch, new() { Name = "Tema claro" })),
            // Overlay/mini drawer closed: the menu button; open drawer: logout (§8.4 rule 4).
            ("Abrir menu | Sair", page.GetByRole(AriaRole.Button, new() { Name = "Abrir menu" })
                .Or(page.GetByRole(AriaRole.Button, new() { Name = "Sair" }))),
        ]),
    ];

    [Fact]
    public async Task Every_page_passes_the_layout_matrix()
    {
        var token = await LoginTokenAsync();
        var output = Path.Combine(Harness.Options.RepoRoot, "TestResults", "layout-matrix");
        var results = new ConcurrentBag<LayoutCase>();
        var combos = from font in Fonts from zoom in Zooms select (Font: font, Zoom: zoom);

        await Parallel.ForEachAsync(combos, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = Cancellation },
            async (combo, _) =>
            {
                foreach (var result in await RunComboAsync(combo.Font, combo.Zoom, token, output))
                {
                    results.Add(result);
                }
            });

        var ordered = results.OrderBy(r => Array.FindIndex(Pages, p => p.Key == r.Page))
            .ThenBy(r => r.Viewport.Width).ThenBy(r => r.Zoom).ThenBy(r => r.Font).ToList();
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), Report(ordered), Cancellation);

        ordered.Should().HaveCount(Pages.Length * Viewports.Length * Zooms.Length * Fonts.Length);
        ordered.Where(r => r.Error is null).Should().OnlyContain(r => r.RootFont == $"{r.Font}px", "the font size emulation must work");
        ordered.Where(r => r.HasIssue).Select(r => r.Describe()).Should().BeEmpty($"every case passes (report: {output}/report.md)");
    }

    /// <summary>
    /// One Chromium profile per (font, zoom): the root font size is the browser's font size setting (profile
    /// preference, needs the full "chromium" channel); zoom z = CSS viewport w/z × h/z with devicePixelRatio z.
    /// </summary>
    private async Task<List<LayoutCase>> RunComboAsync(int font, double zoom, string token, string output)
    {
        var profile = Directory.CreateTempSubdirectory("dib4t-e2e-layout-");
        var results = new List<LayoutCase>();
        try
        {
            Directory.CreateDirectory(Path.Combine(profile.FullName, "Default"));
            await File.WriteAllTextAsync(Path.Combine(profile.FullName, "Default", "Preferences"), JsonSerializer.Serialize(new
            {
                webkit = new { webprefs = new { default_font_size = font, default_fixed_font_size = (int)Math.Round(font * 13 / 16.0) } },
            }), Cancellation);
            await using var context = await Harness.Playwright.Chromium.LaunchPersistentContextAsync(profile.FullName, new()
            {
                Channel = "chromium",
                Headless = !Harness.Options.Headed,
                DeviceScaleFactor = (float)zoom,
                BaseURL = Harness.New.UiBaseUrl.ToString(),
                Locale = "pt-PT",
                TimezoneId = "Europe/Lisbon",
            });
            context.SetDefaultTimeout(15_000);
            // Signed-in pages get the token; the login page none (an open session skips it, §8.1).
            await context.AddInitScriptAsync($$"""
                if (location.pathname.startsWith('/landing')) localStorage.setItem('token', {{JsonSerializer.Serialize(token)}});
                else localStorage.removeItem('token');
                """);
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

            foreach (var viewport in Viewports)
            {
                await page.SetViewportSizeAsync((int)Math.Round(viewport.Width / zoom), (int)Math.Round(viewport.Height / zoom));
                foreach (var layoutPage in Pages)
                {
                    var result = new LayoutCase { Page = layoutPage.Key, Viewport = viewport, Zoom = zoom, Font = font };
                    try
                    {
                        await MeasureAsync(page, layoutPage, result, Path.Combine(output, result.ScreenshotName));
                    }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                    {
                        result.Error = ex.Message.Split('\n')[0];
                    }

                    results.Add(result);
                }
            }
        }
        finally
        {
            profile.Delete(recursive: true);
        }

        return results;
    }

    private static async Task MeasureAsync(IPage page, LayoutPage layoutPage, LayoutCase result, string screenshot)
    {
        await page.GotoAsync("about:blank");
        await page.GotoAsync(layoutPage.Path);
        await page.Locator(layoutPage.Ready).First.WaitForAsync(new() { Timeout = 30_000 });
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.WaitForTimeoutAsync(350); // drawer transition (225 ms)

        Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
        await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true, Scale = ScreenshotScale.Css });
        result.Fill(await page.EvaluateAsync<JsonElement>(LayoutCase.MeasureScript));
        // Before the reachability checks: their hover opens tooltips, which axe would catch mid-fade.
        var axe = await page.RunAxe();
        result.Accessibility.AddRange(axe.Violations
            .Where(v => v.Impact is "serious" or "critical")
            .Select(v => $"{v.Id} ({v.Impact}): {string.Join(", ", v.Nodes.Take(3).Select(n => n.Html))}"));
        foreach (var (name, locator) in layoutPage.PrimaryActions(page))
        {
            try
            {
                await locator.First.ClickAsync(new() { Trial = true, Timeout = 3_000 }); // visible, enabled, receives the click
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                result.Unreachable.Add(name);
            }
        }


    }

    private async Task<string> LoginTokenAsync()
    {
        using var http = new HttpClient { BaseAddress = Harness.New.ApiBaseUrl };
        using var response = await http.PostAsJsonAsync("account/login", new { email = FixtureUser, password = FixturePassword }, Cancellation);
        var token = await response.Content.ReadAsStringAsync(Cancellation);
        response.IsSuccessStatusCode.Should().BeTrue(token);
        return token;
    }

    private static string Report(List<LayoutCase> results)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"# New app layout matrix ({DateTime.Now:yyyy-MM-dd HH:mm})");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"{results.Count(r => r.HasIssue)}/{results.Count} cases with issues. Screenshots: `<page>/<viewport>_z<zoom>_f<font>.png`.");
        report.AppendLine();
        foreach (var result in results)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- {result.Describe()}");
        }

        return report.ToString();
    }

    private sealed record LayoutPage(string Key, string Path, bool SignedIn, string Ready, Func<IPage, (string Name, ILocator Locator)[]> PrimaryActions);
}
