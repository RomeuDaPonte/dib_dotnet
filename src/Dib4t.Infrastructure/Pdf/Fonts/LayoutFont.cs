namespace Dib4t.Infrastructure.Pdf.Fonts;

/// <summary>A shaped glyph as pdfkit keeps it: all values in 1/1000 em (fontkit units × <see cref="LayoutFont.Scale"/>).</summary>
internal readonly record struct ShapedGlyph(int Glyph, double XAdvance, double YAdvance, double XOffset, double YOffset, double AdvanceWidth);

/// <summary>A shaped text: its glyphs and their total advance (1/1000 em).</summary>
internal sealed record ShapedText(IReadOnlyList<ShapedGlyph> Glyphs, double AdvanceWidth);

/// <summary>
/// Port of pdfkit's <c>EmbeddedFont</c> (bundled in the legacy pdfmake): the font pdfmake measures and draws with.
/// One instance per document and font, as legacy (its layout cache and fontkit's glyph cache live per document).
/// Text is shaped in chunks that end after each space or tab, so there is no kerning across a space.
/// </summary>
internal sealed class LayoutFont
{
    private readonly FontkitFont _fontkit;
    private readonly Dictionary<string, ShapedText> _layoutCache = new(StringComparer.Ordinal);

    public LayoutFont(OpenTypeFont font)
    {
        Font = font;
        _fontkit = new FontkitFont(font);
        _fontkit.GetGlyph(0); // this.widths = [this.font.getGlyph(0).advanceWidth]
        Scale = 1000.0 / font.UnitsPerEm;
        Ascender = font.Ascent * Scale;
        Descender = font.Descent * Scale;
        LineGap = font.LineGap * Scale;
    }

    public OpenTypeFont Font { get; }

    /// <summary>The PostScript name (pdfmake's <c>font.name</c>).</summary>
    public string Name => Font.PostScriptName;

    public double Scale { get; }

    public double Ascender { get; }

    public double Descender { get; }

    public double LineGap { get; }

    /// <summary><c>lineHeight(size)</c> (without the line gap).</summary>
    public double LineHeight(double size) => (Ascender + 0 - Descender) / 1000 * size;

    /// <summary>The code points of a glyph (fontkit's glyph object: those of its first lookup), for the ToUnicode map.</summary>
    public IReadOnlyList<int> CodePointsOf(int glyph) => _fontkit.GetGlyph(glyph);

    /// <summary><c>widthOfString(text, size)</c>.</summary>
    public double WidthOfString(string text, double size) => Layout(text).AdvanceWidth * (size / 1000);

    /// <summary><c>layout(text)</c>: the text shaped chunk by chunk (each chunk ends after a space or tab).</summary>
    public ShapedText Layout(string text)
    {
        var glyphs = new List<ShapedGlyph>();
        var advanceWidth = 0.0;
        var last = 0;
        var index = 0;
        while (index <= text.Length)
        {
            if ((index == text.Length && last < index) || (index < text.Length && text[index] is ' ' or '\t'))
            {
                var run = LayoutCached(text[last..Math.Min(++index, text.Length)]);
                glyphs.AddRange(run.Glyphs);
                advanceWidth += run.AdvanceWidth;
                last = index;
            }
            else
            {
                index++;
            }
        }

        return new ShapedText(glyphs, advanceWidth);
    }

    private ShapedText LayoutCached(string text)
    {
        if (_layoutCache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var run = _fontkit.Layout(text);
        var glyphs = new ShapedGlyph[run.Glyphs.Count];
        var advanceWidth = 0.0;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var p = run.Positions[i];
            glyphs[i] = new ShapedGlyph(run.Glyphs[i], p.XAdvance * Scale, p.YAdvance * Scale, p.XOffset * Scale, p.YOffset * Scale,
                Font.AdvanceWidth(run.Glyphs[i]) * Scale);
        }

        foreach (var g in glyphs)
        {
            advanceWidth += g.XAdvance; // GlyphRun#advanceWidth, summed in order
        }

        return _layoutCache[text] = new ShapedText(glyphs, advanceWidth);
    }
}
