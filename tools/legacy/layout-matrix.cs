#:package Microsoft.Playwright@1.64.1
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Phase 0 (PLAN.md §10, §8.4): records the legacy layout ("before" evidence for C9).
// Opens every legacy page in the §8.4 matrix (viewports × browser zoom × root font size), takes a
// full-page screenshot and measures the §8.4 checks:
//   H   horizontal page overflow (documentElement.scrollWidth > innerWidth), in CSS px
//   O   key controls outside the viewport horizontally (not inside a horizontal scroll container)
//   X   overlapping key controls (pairs)
//   T   overlapping text (text runs from different elements drawn over each other)
//   C   key controls or text cut off by an ancestor with overflow hidden (e.g. fixed vh heights)
// The same checks become assertions for the new UI in the E2E suite (§11).
//
// Emulation: browser zoom z = CSS viewport (w/z × h/z) with devicePixelRatio z (what Chromium does
// when zooming). Root font size = Chromium profile preference webkit.webprefs.default_font_size
// (same as the browser's font size setting; needs the full "chromium" channel, not headless shell).
//
// Run: tools/legacy/legacy.sh layout [--pages a,b] [--viewports 360x640,...] [--zooms 1,1.5,2]
//        [--fonts 16,20,24] [--out dir] [--report file.md] [--parallel 3]
// Env: LEGACY_UI, LEGACY_API, LEGACY_EMAIL (default layout.tester@dib4t.local),
//      LEGACY_PASSWORD (default layout-test) — the user created by `legacy.sh seed`.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = ParseArgs(args);
var ui = (Environment.GetEnvironmentVariable("LEGACY_UI") ?? "http://localhost:3000").TrimEnd('/');
var api = (Environment.GetEnvironmentVariable("LEGACY_API") ?? "http://localhost:3001/api").TrimEnd('/');
var email = Environment.GetEnvironmentVariable("LEGACY_EMAIL") ?? "layout.tester@dib4t.local";
var password = Environment.GetEnvironmentVariable("LEGACY_PASSWORD") ?? "layout-test";

var viewports = Get(opts, "viewports", "360x640,768x1024,1366x768,1920x1080,2560x1440")
    .Split(',').Select(v => v.Split('x').Select(int.Parse).ToArray()).Select(a => (W: a[0], H: a[1])).ToArray();
var zooms = Get(opts, "zooms", "1,1.5,2").Split(',').Select(double.Parse).ToArray();
var fonts = Get(opts, "fonts", "16,20,24").Split(',').Select(int.Parse).ToArray();
var outDir = Path.GetFullPath(Get(opts, "out", "artifacts/legacy-layout"));
var reportPath = Path.GetFullPath(Get(opts, "report", "docs/layout/legacy-layout-matrix.md"));
var parallel = int.Parse(Get(opts, "parallel", "3"));

// ---------------------------------------------------------------- login + ids through the legacy API
using var http = new HttpClient();
var loginResp = await http.PostAsJsonAsync($"{api}/account/login", new { email, password });
var token = await loginResp.Content.ReadAsStringAsync();
if (!loginResp.IsSuccessStatusCode) throw new InvalidOperationException($"login failed ({(int)loginResp.StatusCode}): {token}. Run legacy.sh seed on a fresh DB first.");
http.DefaultRequestHeaders.Add("x-auth-token", token);
var budgetsJson = JsonNode.Parse(await http.GetStringAsync($"{api}/budget"))!.AsArray();
var jobsJson = JsonNode.Parse(await http.GetStringAsync($"{api}/job/getAll"))!.AsArray();
// Budget details: the one with the longest descriptive (wrapping); job details: one with comments.
var budgetId = budgetsJson.OrderByDescending(b => b!["descriptive"]?.ToString().Length ?? 0).First()!["_id"]!.ToString();
var jobId = (jobsJson.FirstOrDefault(j => !string.IsNullOrEmpty(j!["comments"]?.ToString())) ?? jobsJson.First())!["_id"]!.ToString();

var pages = new (string Key, string Path)[]
{
    ("login", "/"), ("home", "/landing/home"), ("dashboard", "/landing/dashboard"), ("budgets", "/landing/budgets"),
    ("budget-details", $"/landing/budgets/{budgetId}"), ("jobs", "/landing/job"), ("job-details", $"/landing/job/{jobId}"),
    ("leituras", "/landing/leituras"), ("job-receivings", "/landing/jobReceivings"), ("entities", "/landing/entity"),
    ("settings", "/landing/settings"),
};
if (opts.TryGetValue("pages", out var only)) pages = pages.Where(p => only.Split(',').Contains(p.Key)).ToArray();

Directory.CreateDirectory(outDir);
var total = pages.Length * viewports.Length * zooms.Length * fonts.Length;
Console.WriteLine($"layout matrix: {pages.Length} pages × {viewports.Length} viewports × {zooms.Length} zooms × {fonts.Length} fonts = {total} cases → {outDir}");

// ---------------------------------------------------------------- run
var results = new ConcurrentBag<CaseResult>();
var done = 0;
using var pw = await Playwright.CreateAsync();
var combos = (from f in fonts from z in zooms select (Font: f, Zoom: z)).ToList();
await Parallel.ForEachAsync(combos, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (combo, _) =>
{
    var profile = Path.Combine(Path.GetTempPath(), $"dib4t-layout-{combo.Font}-{combo.Zoom}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(profile, "Default"));
    File.WriteAllText(Path.Combine(profile, "Default", "Preferences"),
        JsonSerializer.Serialize(new { webkit = new { webprefs = new { default_font_size = combo.Font, default_fixed_font_size = (int)Math.Round(combo.Font * 13 / 16.0) } } }));
    try
    {
        await using var ctx = await pw.Chromium.LaunchPersistentContextAsync(profile, new()
        {
            Channel = "chromium", Headless = true, DeviceScaleFactor = (float)combo.Zoom,
            ViewportSize = new() { Width = 1280, Height = 800 }, Locale = "pt-PT", TimezoneId = "Europe/Lisbon",
        });
        await ctx.AddInitScriptAsync($"if (location.pathname.startsWith('/landing')) sessionStorage.setItem('token', {JsonSerializer.Serialize(token)}); else sessionStorage.removeItem('token');");
        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        foreach (var vp in viewports)
        {
            var cssW = (int)Math.Round(vp.W / combo.Zoom);
            var cssH = (int)Math.Round(vp.H / combo.Zoom);
            await page.SetViewportSizeAsync(cssW, cssH);
            foreach (var p in pages)
            {
                var file = Path.Combine(p.Key, $"{vp.W}x{vp.H}_z{combo.Zoom * 100:0}_f{combo.Font}.jpg");
                var r = new CaseResult { Page = p.Key, Viewport = $"{vp.W}x{vp.H}", Zoom = combo.Zoom, Font = combo.Font, CssWidth = cssW, CssHeight = cssH, Screenshot = file };
                try
                {
                    await page.GotoAsync("about:blank");
                    await page.GotoAsync(ui + p.Path, new() { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); } catch (TimeoutException) { }
                    await page.WaitForTimeoutAsync(700); // MUI transitions, chart drawing
                    var m = await page.EvaluateAsync<JsonElement>(MeasureJs);
                    r.Fill(m);
                    Directory.CreateDirectory(Path.Combine(outDir, p.Key));
                    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, file), FullPage = true, Type = ScreenshotType.Jpeg, Quality = 70, Scale = ScreenshotScale.Css });
                }
                catch (Exception ex) { r.Error = ex.Message.Split('\n')[0]; }
                results.Add(r);
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0 || n == total) Console.WriteLine($"  {n}/{total}");
            }
        }
    }
    finally { try { Directory.Delete(profile, true); } catch { } }
});

// ---------------------------------------------------------------- outputs
var ordered = results.OrderBy(r => Array.FindIndex(pages, p => p.Key == r.Page)).ThenBy(r => r.CssWidth * r.Zoom).ThenBy(r => r.Zoom).ThenBy(r => r.Font).ToList();
File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(outDir, "index.html"), Gallery(ordered));
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, Report(ordered));
var failing = ordered.Count(r => r.HasIssue);
Console.WriteLine($"done: {failing}/{ordered.Count} cases with at least one issue; {ordered.Count(r => r.Error != null)} errors");
Console.WriteLine($"report:  {reportPath}\ngallery: {Path.Combine(outDir, "index.html")}");

// ================================================================= helpers
string Report(List<CaseResult> rs)
{
    var sb = new StringBuilder();
    var fontNote = rs.Where(r => r.RootFont != null).GroupBy(r => r.Font).Select(g => $"{g.Key}px→{string.Join("/", g.Select(r => r.RootFont).Distinct())}");
    sb.AppendLine("# Legacy layout matrix (\"before\" evidence for C9)");
    sb.AppendLine();
    sb.AppendLine($"> Generated by `tools/legacy/legacy.sh layout` on {DateTime.Now:yyyy-MM-dd} against the unchanged legacy front-end, seeded with `legacy.sh seed`. Do not edit by hand; re-run the tool.");
    sb.AppendLine($"> Screenshots + `results.json` + `index.html` gallery: `artifacts/legacy-layout/` (not committed, regenerate with the tool). Measured root font sizes: {string.Join(", ", fontNote)}.");
    sb.AppendLine();
    sb.AppendLine("Legend per case: **H**n = page scrolls horizontally by n CSS px · **O**n = n key controls outside the viewport · **X**n = n overlapping control pairs · **T**n = n overlapping text pairs (text drawn over other text) · **C**n = n controls or text lines cut off by an ancestor (`overflow: hidden`, fixed heights) · ✓ = no issue · ERR = page failed to load.");
    sb.AppendLine("Zoom z is emulated as CSS viewport = window / z with devicePixelRatio z. Key controls = buttons, links, inputs, tabs, menu items, checkboxes.");
    sb.AppendLine();
    sb.AppendLine("## Summary");
    sb.AppendLine();
    sb.AppendLine("| Page | Cases with issues | H (cases, max px) | O (cases) | X (cases) | T (cases) | C (cases) |");
    sb.AppendLine("|---|---|---|---|---|---|---|");
    foreach (var g in rs.GroupBy(r => r.Page))
    {
        var l = g.ToList();
        sb.AppendLine($"| {g.Key} | {l.Count(r => r.HasIssue)}/{l.Count} | {l.Count(r => r.HOverflow > 1)}, {l.Max(r => r.HOverflow)} | {l.Count(r => r.Offscreen.Count > 0)} | {l.Count(r => r.Overlaps.Count > 0)} | {l.Count(r => r.TextOverlaps.Count > 0)} | {l.Count(r => r.Clipped.Count > 0)} |");
    }
    foreach (var g in rs.GroupBy(r => r.Page))
    {
        sb.AppendLine();
        sb.AppendLine($"## {g.Key}");
        sb.AppendLine();
        var cols = (from z in zooms from f in fonts select (z, f)).ToList();
        sb.AppendLine("| Viewport | " + string.Join(" | ", cols.Select(c => $"{c.z * 100:0}% / {c.f}px")) + " |");
        sb.AppendLine("|---|" + string.Concat(cols.Select(_ => "---|")));
        foreach (var vp in viewports)
        {
            var row = cols.Select(c => g.FirstOrDefault(r => r.Viewport == $"{vp.W}x{vp.H}" && r.Zoom == c.z && r.Font == c.f)?.Flags ?? "–");
            sb.AppendLine($"| {vp.W}×{vp.H} | " + string.Join(" | ", row) + " |");
        }
        var samples = new List<string>();
        void Top(string what, IEnumerable<string> items)
        {
            var top = items.GroupBy(x => x).OrderByDescending(x => x.Count()).Take(6).Select(x => $"`{x.Key}` ({x.Count()})").ToList();
            if (top.Count > 0) samples.Add($"- {what}: {string.Join(", ", top)}");
        }
        Top("Outside the viewport", g.SelectMany(r => r.Offscreen));
        Top("Overlapping controls", g.SelectMany(r => r.Overlaps));
        Top("Overlapping text", g.SelectMany(r => r.TextOverlaps));
        Top("Cut off", g.SelectMany(r => r.Clipped));
        Top("Errors", g.Where(r => r.Error != null).Select(r => r.Error!));
        if (samples.Count > 0) { sb.AppendLine(); sb.AppendLine("Most frequent offenders (number of cases):"); samples.ForEach(s => sb.AppendLine(s)); }
    }
    return sb.ToString();
}

static string Gallery(List<CaseResult> rs)
{
    var sb = new StringBuilder();
    sb.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Legacy layout matrix</title><style>
:root{color-scheme:light dark;--bg:#fff;--fg:#222;--muted:#666;--bad:#c62828;--ok:#2e7d32;--card:#f4f4f4}
@media (prefers-color-scheme:dark){:root{--bg:#1e1e1e;--fg:#eee;--muted:#aaa;--bad:#ef9a9a;--ok:#a5d6a7;--card:#2a2a2a}}
body{background:var(--bg);color:var(--fg);font:14px/1.4 system-ui,sans-serif;margin:0 16px 40px}
h2{margin-top:32px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px}
figure{margin:0;background:var(--card);border-radius:6px;padding:6px}figure img{width:100%;height:180px;object-fit:cover;object-position:top left;display:block}
figcaption{font-size:12px;color:var(--muted)}.bad{color:var(--bad);font-weight:600}.ok{color:var(--ok)}
label{margin-right:12px}</style></head><body><h1>Legacy layout matrix</h1>
<p><label><input type="checkbox" id="only"> only cases with issues</label></p>
""");
    foreach (var g in rs.GroupBy(r => r.Page))
    {
        sb.Append($"<h2 id=\"{g.Key}\">{g.Key}</h2><div class=\"grid\">");
        foreach (var r in g)
        {
            var src = r.Screenshot.Replace('\\', '/');
            var cls = r.HasIssue ? "bad" : "ok";
            var title = System.Net.WebUtility.HtmlEncode(string.Join(" | ", r.Offscreen.Concat(r.Overlaps).Concat(r.TextOverlaps).Concat(r.Clipped).Take(12)));
            sb.Append($"<figure data-issue=\"{(r.HasIssue ? 1 : 0)}\"><a href=\"{src}\" target=\"_blank\"><img loading=\"lazy\" src=\"{src}\" alt=\"{r.Page} {r.Viewport}\"></a>" +
                      $"<figcaption>{r.Viewport} · {r.Zoom * 100:0}% · {r.Font}px (css {r.CssWidth}×{r.CssHeight})<br><span class=\"{cls}\" title=\"{title}\">{r.Flags}</span></figcaption></figure>");
        }
        sb.Append("</div>");
    }
    sb.Append("<script>document.getElementById('only').onchange=e=>document.querySelectorAll('figure').forEach(f=>f.style.display=e.target.checked&&f.dataset.issue==='0'?'none':'')</script></body></html>");
    return sb.ToString();
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (var i = 0; i < a.Length - 1; i++) if (a[i].StartsWith("--")) d[a[i][2..]] = a[++i];
    return d;
}
static string Get(Dictionary<string, string> d, string k, string def) => d.TryGetValue(k, out var v) ? v : def;

partial class Program
{
    const string MeasureJs = """
() => {
  const vw = window.innerWidth, de = document.documentElement;
  const label = el => ((el.getAttribute('aria-label') || el.innerText || el.value || el.placeholder || el.getAttribute('name') || el.getAttribute('title') || '').trim().replace(/\s+/g, ' ').slice(0, 40)) || el.tagName.toLowerCase();
  // Screen-reader-only content (e.g. Google Charts' data table at left:-10000px) is not layout.
  const visible = el => { const r = el.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false;
    if (r.right < 0 || r.bottom < -window.scrollY) return false;
    for (let p = el; p && p !== document.body; p = p.parentElement) { const s = getComputedStyle(p); if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity === 0) return false; } return true; };
  const inScrollX = el => { for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) { const s = getComputedStyle(p);
    if ((s.overflowX === 'auto' || s.overflowX === 'scroll') && p.scrollWidth > p.clientWidth + 1) return true; } return false; };
  const sel = 'button, a[href], input:not([type=hidden]), select, textarea, [role=button], [role=tab], [role=menuitem], [role=checkbox], [role=combobox]';
  const all = [...document.querySelectorAll(sel)].filter(visible);
  const ctl = all.filter(c => !all.some(o => o !== c && o.contains(c)));
  const rects = ctl.map(el => ({ el, r: el.getBoundingClientRect(), name: label(el) }));
  const offscreen = rects.filter(x => (x.r.right > vw + 1 || x.r.left < -1) && !inScrollX(x.el)).map(x => x.name);
  const overlaps = [];
  for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) {
    const a = rects[i].r, b = rects[j].r;
    const w = Math.min(a.right, b.right) - Math.max(a.left, b.left), h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (w > 2 && h > 2 && w * h > 0.15 * Math.min(a.width * a.height, b.width * b.height)) overlaps.push(rects[i].name + ' ↔ ' + rects[j].name);
  }
  // Text runs: one rect per rendered line of every visible text node.
  const texts = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, { acceptNode: n => n.data.trim() ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT });
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const el = n.parentElement;
    if (!el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TITLE'].includes(el.tagName) || !visible(el)) continue;
    const range = document.createRange(); range.selectNodeContents(n);
    for (const r of range.getClientRects()) if (r.width > 1 && r.height > 1) texts.push({ el, r, name: n.data.trim().replace(/\s+/g, ' ').slice(0, 30) });
  }
  const textOverlaps = [];
  for (let i = 0; i < texts.length; i++) for (let j = i + 1; j < texts.length; j++) {
    const a = texts[i], b = texts[j];
    if (a.el === b.el) continue;
    const w = Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left), h = Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top);
    if (w > 2 && h > 2 && w * h > 0.2 * Math.min(a.r.width * a.r.height, b.r.width * b.r.height)) textOverlaps.push(a.name + ' ↔ ' + b.name);
  }
  const clipped = [];
  for (const x of rects.concat(texts)) {
    for (let p = x.el.parentElement; p && p !== document.body; p = p.parentElement) {
      const s = getComputedStyle(p);
      if (s.overflowX === 'visible' && s.overflowY === 'visible') continue;
      if (s.overflowX === 'auto' || s.overflowX === 'scroll' || s.overflowY === 'auto' || s.overflowY === 'scroll') break; // reachable by scrolling
      const pr = p.getBoundingClientRect();
      if (x.r.bottom > pr.bottom + 2 || x.r.right > pr.right + 2 || x.r.top < pr.top - 2 || x.r.left < pr.left - 2) { clipped.push(x.name); break; }
    }
  }
  return { scrollWidth: de.scrollWidth, innerWidth: vw, scrollHeight: de.scrollHeight, rootFont: getComputedStyle(de).fontSize,
           dpr: window.devicePixelRatio, controls: ctl.length, offscreen: offscreen.slice(0, 40), overlaps: overlaps.slice(0, 40), textOverlaps: [...new Set(textOverlaps)].slice(0, 40), clipped: [...new Set(clipped)].slice(0, 40) };
}
""";
}

class CaseResult
{
    public string Page { get; set; } = "";
    public string Viewport { get; set; } = "";
    public double Zoom { get; set; }
    public int Font { get; set; }
    public int CssWidth { get; set; }
    public int CssHeight { get; set; }
    public string Screenshot { get; set; } = "";
    public string? RootFont { get; set; }
    public double Dpr { get; set; }
    public int Controls { get; set; }
    public int HOverflow { get; set; }
    public int ScrollHeight { get; set; }
    public List<string> Offscreen { get; set; } = [];
    public List<string> Overlaps { get; set; } = [];
    public List<string> Clipped { get; set; } = [];
    public List<string> TextOverlaps { get; set; } = [];
    public string? Error { get; set; }

    public bool HasIssue => Error != null || HOverflow > 1 || Offscreen.Count > 0 || Overlaps.Count > 0 || TextOverlaps.Count > 0 || Clipped.Count > 0;
    public string Flags => Error != null ? "ERR" : !HasIssue ? "✓" : string.Join(" ", new[]
    {
        HOverflow > 1 ? $"H{HOverflow}" : null, Offscreen.Count > 0 ? $"O{Offscreen.Count}" : null,
        Overlaps.Count > 0 ? $"X{Overlaps.Count}" : null, TextOverlaps.Count > 0 ? $"T{TextOverlaps.Count}" : null, Clipped.Count > 0 ? $"C{Clipped.Count}" : null,
    }.Where(s => s != null));

    public void Fill(System.Text.Json.JsonElement m)
    {
        HOverflow = Math.Max(0, m.GetProperty("scrollWidth").GetInt32() - m.GetProperty("innerWidth").GetInt32());
        ScrollHeight = m.GetProperty("scrollHeight").GetInt32();
        RootFont = m.GetProperty("rootFont").GetString();
        Dpr = m.GetProperty("dpr").GetDouble();
        Controls = m.GetProperty("controls").GetInt32();
        Offscreen = m.GetProperty("offscreen").EnumerateArray().Select(e => e.GetString()!).ToList();
        Overlaps = m.GetProperty("overlaps").EnumerateArray().Select(e => e.GetString()!).ToList();
        Clipped = m.GetProperty("clipped").EnumerateArray().Select(e => e.GetString()!).ToList();
        TextOverlaps = m.GetProperty("textOverlaps").EnumerateArray().Select(e => e.GetString()!).ToList();
    }
}
