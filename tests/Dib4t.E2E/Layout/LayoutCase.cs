using System.Globalization;
using System.Text.Json;

namespace Dib4t.E2E.Layout;

/// <summary>
/// One case of the §8.4 layout matrix (page × viewport × browser zoom × root font size) and what was measured.
/// The measurements are the ones of the legacy "before" run (<c>tools/legacy/layout-matrix.cs</c>,
/// <c>docs/layout/legacy-layout-matrix.md</c>), so both apps are judged by the same code.
/// </summary>
public sealed class LayoutCase
{
    public required string Page { get; init; }

    public required (int Width, int Height) Viewport { get; init; }

    public required double Zoom { get; init; }

    public required int Font { get; init; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"{Page} {Viewport.Width}x{Viewport.Height} z{Zoom * 100:0} f{Font}");

    public string ScreenshotName => string.Create(CultureInfo.InvariantCulture, $"{Page}/{Viewport.Width}x{Viewport.Height}_z{Zoom * 100:0}_f{Font}.png");

    /// <summary>H: the page scrolls horizontally by this many CSS px.</summary>
    public int HorizontalOverflow { get; private set; }

    /// <summary>The root font size the page sees (checks that the font-size emulation worked).</summary>
    public string? RootFont { get; private set; }

    /// <summary>O: key controls outside the viewport (not inside a horizontal scroll container).</summary>
    public List<string> Offscreen { get; } = [];

    /// <summary>X: overlapping key controls (pairs).</summary>
    public List<string> Overlaps { get; } = [];

    /// <summary>T: text drawn over other text (pairs).</summary>
    public List<string> TextOverlaps { get; } = [];

    /// <summary>C: controls or text lines cut off by an ancestor that does not scroll.</summary>
    public List<string> Clipped { get; } = [];

    /// <summary>Primary actions that are not visible or not clickable.</summary>
    public List<string> Unreachable { get; } = [];

    /// <summary>axe-core violations with impact serious or critical.</summary>
    public List<string> Accessibility { get; } = [];

    public string? Error { get; set; }

    public bool HasIssue => Error is not null || HorizontalOverflow > 1 || Offscreen.Count > 0 || Overlaps.Count > 0
        || TextOverlaps.Count > 0 || Clipped.Count > 0 || Unreachable.Count > 0 || Accessibility.Count > 0;

    public string Describe()
    {
        if (Error is not null)
        {
            return $"{Name}: ERROR {Error}";
        }

        var parts = new List<string>();
        if (HorizontalOverflow > 1)
        {
            parts.Add($"H{HorizontalOverflow}");
        }

        void Add(string flag, List<string> items)
        {
            if (items.Count > 0)
            {
                parts.Add($"{flag}{items.Count} [{string.Join("; ", items.Take(5))}]");
            }
        }

        Add("O", Offscreen);
        Add("X", Overlaps);
        Add("T", TextOverlaps);
        Add("C", Clipped);
        Add("unreachable ", Unreachable);
        Add("axe ", Accessibility);
        return $"{Name}: {(parts.Count == 0 ? "ok" : string.Join(" ", parts))}";
    }

    public void Fill(JsonElement measured)
    {
        HorizontalOverflow = Math.Max(0, measured.GetProperty("scrollWidth").GetInt32() - measured.GetProperty("innerWidth").GetInt32());
        RootFont = measured.GetProperty("rootFont").GetString();
        Offscreen.AddRange(Strings(measured, "offscreen"));
        Overlaps.AddRange(Strings(measured, "overlaps"));
        TextOverlaps.AddRange(Strings(measured, "textOverlaps"));
        Clipped.AddRange(Strings(measured, "clipped"));
    }

    private static IEnumerable<string> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!);

    /// <summary>
    /// The legacy tool's measurement (same selectors, thresholds and rules), returned as JSON. Kept in step with
    /// <c>tools/legacy/layout-matrix.cs</c>: both apps must be measured the same way. The optional argument (a CSS
    /// selector) limits the control and text checks to that element, for states the legacy run did not measure (an
    /// open modal dialog); without it the script measures the whole page exactly as the legacy tool does.
    /// </summary>
    public const string MeasureScript = """
        (scope) => {
          const vw = window.innerWidth, de = document.documentElement;
          const root = (scope && document.querySelector(scope)) || document.body;
          const label = el => ((el.getAttribute('aria-label') || el.innerText || el.value || el.placeholder || el.getAttribute('name') || el.getAttribute('title') || '').trim().replace(/\s+/g, ' ').slice(0, 40)) || el.tagName.toLowerCase();
          const visible = el => { const r = el.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false;
            if (r.right < 0 || r.bottom < -window.scrollY) return false;
            for (let p = el; p && p !== document.body; p = p.parentElement) { const s = getComputedStyle(p); if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity === 0) return false; } return true; };
          const inScrollX = el => { for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) { const s = getComputedStyle(p);
            if ((s.overflowX === 'auto' || s.overflowX === 'scroll') && p.scrollWidth > p.clientWidth + 1) return true; } return false; };
          const sel = 'button, a[href], input:not([type=hidden]), select, textarea, [role=button], [role=tab], [role=menuitem], [role=checkbox], [role=combobox]';
          const all = [...root.querySelectorAll(sel)].filter(visible);
          const ctl = all.filter(c => !all.some(o => o !== c && o.contains(c)));
          const rects = ctl.map(el => ({ el, r: el.getBoundingClientRect(), name: label(el) }));
          const offscreen = rects.filter(x => (x.r.right > vw + 1 || x.r.left < -1) && !inScrollX(x.el)).map(x => x.name);
          const overlaps = [];
          for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) {
            const a = rects[i].r, b = rects[j].r;
            const w = Math.min(a.right, b.right) - Math.max(a.left, b.left), h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
            if (w > 2 && h > 2 && w * h > 0.15 * Math.min(a.width * a.height, b.width * b.height)) overlaps.push(rects[i].name + ' ↔ ' + rects[j].name);
          }
          const texts = [];
          const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, { acceptNode: n => n.data.trim() ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT });
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
              if (s.overflowX === 'auto' || s.overflowX === 'scroll' || s.overflowY === 'auto' || s.overflowY === 'scroll') break;
              const pr = p.getBoundingClientRect();
              if (x.r.bottom > pr.bottom + 2 || x.r.right > pr.right + 2 || x.r.top < pr.top - 2 || x.r.left < pr.left - 2) { clipped.push(x.name); break; }
            }
          }
          return { scrollWidth: de.scrollWidth, innerWidth: vw, rootFont: getComputedStyle(de).fontSize,
                   offscreen: offscreen.slice(0, 40), overlaps: overlaps.slice(0, 40), textOverlaps: [...new Set(textOverlaps)].slice(0, 40), clipped: [...new Set(clipped)].slice(0, 40) };
        }
        """;
}
