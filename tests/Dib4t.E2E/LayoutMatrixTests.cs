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
        new("settings-users", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Filtrar", page.Locator("#users-filter")),
            ("Add User", page.GetByRole(AriaRole.Button, new() { Name = "Add User" })),
            ("Info", Tab(page, "Info")),
        ]),
        new("settings-user-dialog", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Nome do utilizador", page.Locator("#user-name")),
            ("Concluir", page.GetByRole(AriaRole.Button, new() { Name = "Concluir" })),
        ], Open: async page =>
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Add User" }).ClickAsync();
            await page.Locator("#user-name").WaitForAsync();
        }, Scope: ".mud-dialog"),
        new("settings-prices", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Editar preços", page.GetByRole(AriaRole.Button, new() { Name = "Editar preços" })),
        ], Open: page => OpenTabAsync(page, "Preços", ".settings-prices tbody td")),
        new("settings-payment-methods", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Adicinar", page.Locator("#payment-method-new")),
        ], Open: page => OpenTabAsync(page, "M/Pagamento", "#payment-method-new")),
        new("settings-printers", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("IP", page.Locator("#printer-ip")),
            ("Guardar", page.GetByRole(AriaRole.Button, new() { Name = "Guardar" })),
            ("Nova Impressora", page.GetByRole(AriaRole.Button, new() { Name = "Nova Impressora" })),
        ], Open: page => OpenTabAsync(page, "Impressoras", "#printer-ip")),
        new("settings-logs", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            // Wide: the search box and button; phones: the filters fold behind "Filtros" (§3.3).
            ("Pesquisar | Filtros", page.GetByRole(AriaRole.Button, new() { Name = "Pesquisar", Exact = true })
                .Or(page.GetByRole(AriaRole.Button, new() { Name = "Filtros" })).Filter(new() { Visible = true })),
            ("Info", Tab(page, "Info")),
        ], Open: OpenLogsWithRowsAsync),
        new("settings-logs-expanded", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Pedido", page.Locator(".logs-detail .logs-request").First),
        ], Open: async page =>
        {
            await OpenLogsWithRowsAsync(page);
            await page.Locator(".logs-expand").First.ClickAsync();
            await page.Locator(".logs-detail .logs-request").First.WaitForAsync();
        }),
        new("settings-database", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Transferir Backup", page.GetByRole(AriaRole.Button, new() { Name = "Transferir Backup" })),
            ("Restaurar Backup", page.GetByRole(AriaRole.Button, new() { Name = "Restaurar Backup" })),
            ("Atualizar", page.GetByRole(AriaRole.Button, new() { Name = "Atualizar" })),
            ("budgets", page.Locator(".database-table tbody td").First),
        ], Open: page => OpenTabAsync(page, "Base de Dados", ".database-table tbody tr")),
        new("settings-database-dialog", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Ficheiro", page.Locator(".database-file-box")),
            ("Cancelar", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancelar" })),
            ("Restaurar", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Restaurar", Exact = true })),
        ], Open: async page =>
        {
            await OpenTabAsync(page, "Base de Dados", ".database-table tbody tr");
            var chooser = await page.RunAndWaitForFileChooserAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Restaurar Backup" }).ClickAsync());
            await chooser.SetFilesAsync(new FilePayload
            {
                // The longest name a downloaded backup has, plus a copy suffix.
                Name = "dib4t-backup-2026-11-11_16-15-31 (1).dibbak",
                MimeType = "application/octet-stream",
                Buffer = [1, 2, 3],
            });
            await page.Locator(".database-file-box").WaitForAsync();
            await page.Mouse.MoveAsync(0, 0);
        }, Scope: ".mud-dialog"),
        // The overlay of a running backup, held open: the download is never answered (the next case loads the page again).
        new("settings-database-overlay", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("A criar backup", page.Locator(".database-overlay-title")),
            ("Não feche", page.Locator(".database-overlay-caption")),
        ], Open: async page =>
        {
            await OpenTabAsync(page, "Base de Dados", ".database-table tbody tr");
            await page.RouteAsync("**/api/database/backup", _ => Task.CompletedTask);
            // The page asks before it is left while a backup runs (legacy beforeunload): the next case's navigation answers yes.
            page.Dialog -= AcceptLeave;
            page.Dialog += AcceptLeave;
            await page.GetByRole(AriaRole.Button, new() { Name = "Transferir Backup" }).ClickAsync();
            await page.Locator(".database-overlay-title").WaitForAsync();
        }, Scope: ".database-overlay", Reset: page => page.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.IgnoreErrors })),
        new("settings-info", "/landing/settings", SignedIn: true, Ready: ".settings-table tbody tr", page =>
        [
            ("Utilizadores", Tab(page, "Utilizadores")),
        ], Open: page => OpenTabAsync(page, "Info", ".settings-info")),
        new("entities", "/landing/entity", SignedIn: true, Ready: ".entities-table tbody tr td", page =>
        [
            ("filtrar", page.Locator("#entities-filter")),
            ("Add Entidade", page.GetByRole(AriaRole.Button, new() { Name = "Add Entidade" })),
            ("Editar", page.Locator(".entities-table tbody tr").First.GetByRole(AriaRole.Button)),
        ]),
        new("entities-dialog", "/landing/entity", SignedIn: true, Ready: ".entities-table tbody tr td", page =>
        [
            ("Entidade", page.Locator("#entity-name")),
            ("Modo de pagamento", page.Locator("#entity-payment-condition")),
            ("Concluir", page.GetByRole(AriaRole.Button, new() { Name = "Concluir" })),
        ], Open: async page =>
        {
            await page.Locator(".entities-table tbody tr").First.GetByRole(AriaRole.Button).ClickAsync();
            await page.Locator("#entity-name").WaitForAsync();
        }, Scope: ".mud-dialog"),
        new("budgets", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Filtrar", page.Locator("#budgets-filter")),
            ("Novo orçamento", page.GetByRole(AriaRole.Button, new() { Name = "Novo orçamento" })),
            ("Editar", page.Locator(".budgets-table tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Editar" })),
        ]),
        new("budgets-revisions", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Nª da revisão", page.Locator(".budgets-revisions-table").First),
        ], Open: async page =>
        {
            await page.Locator(".budgets-revisions-toggle").First.ClickAsync();
            await page.Locator(".budgets-revisions-table").First.WaitForAsync();
        }),
        new("budgets-dialog", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Cliente", page.Locator("#budget-client")),
            ("Realizado por", page.Locator("#budget-made-by")),
            ("Concluir", page.GetByRole(AriaRole.Button, new() { Name = "Concluir" })),
        ], Open: async page =>
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Novo orçamento" }).ClickAsync();
            await page.Locator("#budget-descriptive").WaitForAsync();
        }, Scope: ".mud-dialog"),
        new("budget-details", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Editar orçamento", page.GetByRole(AriaRole.Button, new() { Name = "Editar orçamento" })),
            ("Alternar", page.GetByRole(AriaRole.Button, new() { Name = "Alternar" })),
            ("Margem", page.Locator("#budget-margin")),
            ("Total", page.Locator("#budget-total")),
            ("Prazo / Dias", page.Locator("#budget-days")),
            ("M / Pagamento", page.Locator("#budget-payment")),
            ("Qtd", page.Locator(".budget-task").First.Locator("input[id$='-quantity']")),
            ("Unit", page.Locator(".budget-task").First.Locator("input[id$='-unit-price']")),
            ("Adicionar", page.Locator(".budget-task").First.GetByRole(AriaRole.Button, new() { Name = "Adicionar" })),
        ], Open: OpenBudgetDetailsAsync),
        new("budget-details-description", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Descrição", page.Locator("#budget-description")),
            ("Concluir", page.GetByRole(AriaRole.Button, new() { Name = "Concluir" })),
        ], Open: async page =>
        {
            await OpenBudgetDetailsAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Alternar" }).ClickAsync();
            await page.Locator("#budget-description").WaitForAsync();
            await page.Mouse.MoveAsync(0, 0); // off the icon: its tooltip would be open during the checks
        }),
        new("job-details", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Data de entrega", page.Locator("#job-delivery-date")),
            ("Percentagem faturada", page.Locator("#job-invoiced-percent")),
            ("Identificação da encomenda", page.Locator("#job-order-identification")),
            ("Desenvolvimento: fim", page.Locator("#job-devEndedInDate")),
            ("Registo", page.Locator("#job-register-development")),
            ("Observações", page.Locator("#job-comments")),
            ("Controlo de produto", page.Locator(".job-checks").First.Locator("input[type=checkbox]").First),
        ], Open: OpenJobDetailsAsync),
        new("job-print-dialog", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Produção", page.Locator(".jobs-print-row").Filter(new() { HasTextString = "Produção" }).Locator("input[type=checkbox]")),
            ("Maquinação", page.Locator(".jobs-print-row").Filter(new() { HasTextString = "Maquinação" }).Locator("input[type=checkbox]")),
            ("Cancelar", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true })),
            ("Imprimir", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Imprimir", Exact = true })),
        ], Open: async page =>
        {
            await page.Locator(".budgets-table tbody tr").Filter(new() { Has = page.Locator(".budgets-has-job") }).First
                .Locator("button[aria-label^='Opções de']").ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Imprimir" }).ClickAsync();
            await page.Locator(".jobs-print-row").First.WaitForAsync();
            await page.Mouse.MoveAsync(0, 0);
        }, Scope: ".mud-dialog"),
        new("dashboard", "/landing/dashboard", SignedIn: true, Ready: ".mud-chart-legend-item", page =>
        [
            ("Gráfico", page.Locator("[data-testid=dashboard-chart] svg").First),
            ("1º teste", page.Locator(".mud-chart-legend-item").Filter(new() { HasTextString = "1º teste" })),
        ]),
        new("readings", "/landing/leituras", SignedIn: true, Ready: "#readings-budget-number", page =>
        [
            ("N Orçamento", page.Locator("#readings-budget-number")),
        ]),
        // The longest state ("Desenvolvimento", answered by the test) in the dialog, held open while it is measured: once
        // shown, the page's pending timers are cleared, the .NET one that closes it after 3 s with them (a paused
        // Playwright clock would also stop axe's timers). The next case loads the page again.
        new("readings-dialog", "/landing/leituras", SignedIn: true, Ready: "#readings-budget-number", page =>
        [
            ("Estado", page.Locator("[data-testid=readings-state]")),
            ("Sair", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Sair", Exact = true })),
        ], Open: async page =>
        {
            await page.RouteAsync("**/api/job/getByBudgetNumber/**", route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json; charset=utf-8",
                Body = """{"finalPrice":100,"state":"Desenvolvimento","orderIdentification":"","commentType":[],"_id":"6a1f00000000000000000001"}""",
            }));
            await page.Locator("#readings-budget-number").FillAsync("26-0001");
            await page.Locator("#readings-budget-number").PressAsync("Enter");
            await page.Locator("[data-testid=readings-state]").Filter(new() { HasTextString = "Desenvolvimento" }).WaitForAsync();
            await page.EvaluateAsync("() => { const last = setTimeout(() => {}, 0); for (let id = 0; id <= last; id++) clearTimeout(id); }");
        }, Scope: ".mud-dialog", Reset: page => page.UnrouteAllAsync()),
        new("budget-details-import", "/landing/budgets", SignedIn: true, Ready: ".budgets-table tbody tr td", page =>
        [
            ("Nª Orçamento", page.Locator("#import-number")),
            ("Importar", page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Importar", Exact = true })),
        ], Open: async page =>
        {
            await OpenBudgetDetailsAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Importar" }).ClickAsync();
            await page.Locator("#import-number").WaitForAsync();
            // The pointer stays where the header's icon was: on some sizes that is over "Cancelar", whose hover
            // background axe would measure instead of the button's normal state.
            await page.Mouse.MoveAsync(0, 0);
        }, Scope: ".mud-dialog"),
    ];

    /// <summary>The details of an editable budget (no job, not locked: the import button shows), opened from the list.</summary>
    private static async Task OpenBudgetDetailsAsync(IPage page)
    {
        await page.Locator(".budgets-table tbody tr").Filter(new() { HasNot = page.Locator(".budgets-has-job") })
            .Filter(new() { Has = page.Locator(".budgets-incomplete") }).First
            .GetByRole(AriaRole.Button, new() { Name = "Editar" }).ClickAsync();
        await page.Locator(".budget-task").First.WaitForAsync();
        await page.Mouse.MoveAsync(0, 0); // the pointer would rest on a header icon and open its tooltip during the checks
    }

    /// <summary>The details of a job, opened from the budget list's "Detalhes obra" (the first budget with a job).</summary>
    private static async Task OpenJobDetailsAsync(IPage page)
    {
        await page.Locator(".budgets-table tbody tr").Filter(new() { Has = page.Locator(".budgets-has-job") }).First
            .Locator("button[aria-label^='Opções de']").ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Detalhes obra" }).ClickAsync();
        await page.Locator(".job-cost-row input").First.WaitForAsync();
        await page.Mouse.MoveAsync(0, 0);
    }

    /// <summary>
    /// The pages to run: all of them, or only the keys in <c>DIB4T_LAYOUT_PAGES</c> (comma-separated, e.g.
    /// <c>budgets,budgets-dialog</c>) while working on a page. <c>tools/verify.sh</c> leaves it unset.
    /// </summary>
    private static LayoutPage[] Selected { get; } =
        Environment.GetEnvironmentVariable("DIB4T_LAYOUT_PAGES") is { Length: > 0 } keys
            ? [.. Pages.Where(p => keys.Split(',', StringSplitOptions.TrimEntries).Contains(p.Key, StringComparer.Ordinal))]
            : Pages;

    private static async void AcceptLeave(object? sender, IDialog dialog)
    {
        try
        {
            await dialog.AcceptAsync();
        }
        catch (PlaywrightException)
        {
            // already answered, or the page is closing
        }
    }

    private static ILocator Tab(IPage page, string name) => page.GetByRole(AriaRole.Tab, new() { Name = name });

    /// <summary>Registos with entries (the store commits every 2 s: re-opening the tab searches again).</summary>
    private static async Task OpenLogsWithRowsAsync(IPage page)
    {
        for (var attempt = 0; ; attempt++)
        {
            await OpenTabAsync(page, "Registos", ".logs-table"); // the search box is folded away on phones
            try
            {
                await page.Locator("tr.logs-row").First.WaitForAsync(new() { Timeout = 3_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 5)
            {
                await Tab(page, "Info").ClickAsync();
            }
        }
    }

    private static async Task OpenTabAsync(IPage page, string tab, string ready)
    {
        await Tab(page, tab).ClickAsync();
        await page.Locator(ready).First.WaitForAsync();
    }

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

        ordered.Should().HaveCount(Selected.Length * Viewports.Length * Zooms.Length * Fonts.Length);
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
                foreach (var layoutPage in Selected)
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
                    finally
                    {
                        if (layoutPage.Reset is not null)
                        {
                            await layoutPage.Reset(page);
                        }
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
        if (layoutPage.Open is not null)
        {
            await layoutPage.Open(page);
            await page.EvaluateAsync("() => window.scrollTo(0, 0)"); // a click may have scrolled the page under the sticky app bar
        }

        await page.EvaluateAsync("() => document.fonts.ready");
        await page.WaitForTimeoutAsync(350); // drawer transition (225 ms)

        Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
        await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true, Scale = ScreenshotScale.Css });
        result.Fill(await page.EvaluateAsync<JsonElement>(LayoutCase.MeasureScript, layoutPage.Scope));
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

    /// <summary>
    /// A page of the matrix. <c>Open</c> runs after <c>Ready</c> and brings it into the state to measure (a tab, a
    /// dialog); <c>Scope</c> limits the control/text checks to one element (an open modal dialog: what is under it
    /// cannot be used); the page-scroll check and axe always cover the whole page. <c>Reset</c> runs after the case,
    /// failed or not (e.g. a paused clock or a route of <c>Open</c>).
    /// </summary>
    private sealed record LayoutPage(
        string Key, string Path, bool SignedIn, string Ready, Func<IPage, (string Name, ILocator Locator)[]> PrimaryActions,
        Func<IPage, Task>? Open = null, string? Scope = null, Func<IPage, Task>? Reset = null);
}
