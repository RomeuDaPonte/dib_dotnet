using Dib4t.Infrastructure.Pdf.Fonts;
using Dib4t.Infrastructure.Pdf.Text;

namespace Dib4t.Infrastructure.Pdf.Layout;

/// <summary>A styled piece of a text node (pdfmake text item): the text with its font size, weight and alignment.</summary>
internal sealed record TextRun(string Text, double FontSize, bool Bold, string Alignment = "left");

/// <summary>The fonts of one document (pdfmake's font provider: Roboto, normal = Regular, bold = Medium).</summary>
internal sealed class DocumentFonts
{
    public LayoutFont Regular { get; } = new(OpenTypeFont.Regular);

    public LayoutFont Medium { get; } = new(OpenTypeFont.Medium);

    public LayoutFont For(bool bold) => bold ? Medium : Regular;
}

/// <summary>pdfmake's inline: a word (with its trailing spaces) as measured by <c>TextTools.measure</c>.</summary>
internal sealed class Inline
{
    public required string Text { get; set; }

    public required LayoutFont Font { get; init; }

    public required double FontSize { get; init; }

    public required string Alignment { get; init; }

    public bool LineEnd { get; init; }

    public double Width { get; set; }

    public double Height { get; set; }

    public double LeadingCut { get; set; }

    public double TrailingCut { get; set; }

    /// <summary>Position in the line (set by <see cref="Line.AddInline"/>, moved by justification).</summary>
    public double X { get; set; }

    public Inline Clone() => (Inline)MemberwiseClone();
}

/// <summary>pdfmake's <c>Line</c>.</summary>
internal sealed class Line(double maxWidth)
{
    private double _leadingCut;
    private double _trailingCut;
    private double _inlineWidths;

    public double MaxWidth { get; } = maxWidth;

    public List<Inline> Inlines { get; } = [];

    public bool NewLineForced { get; private set; }

    public bool LastLineInParagraph { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double AscenderHeight()
    {
        var y = 0.0;
        foreach (var inline in Inlines)
        {
            y = Math.Max(y, inline.Font.Ascender / 1000 * inline.FontSize);
        }

        return y;
    }

    public bool HasEnoughSpaceForInline(Inline inline)
    {
        if (Inlines.Count == 0)
        {
            return true;
        }

        return !NewLineForced && _inlineWidths + inline.Width - _leadingCut - inline.TrailingCut <= MaxWidth;
    }

    public void AddInline(Inline inline)
    {
        if (Inlines.Count == 0)
        {
            _leadingCut = inline.LeadingCut;
        }

        _trailingCut = inline.TrailingCut;
        inline.X = _inlineWidths - _leadingCut;
        Inlines.Add(inline);
        _inlineWidths += inline.Width;
        if (inline.LineEnd)
        {
            NewLineForced = true;
        }
    }

    public double Width => _inlineWidths - _leadingCut - _trailingCut;

    public double Height
    {
        get
        {
            var max = 0.0;
            foreach (var item in Inlines)
            {
                max = Math.Max(max, item.Height);
            }

            return max;
        }
    }
}

/// <summary>pdfmake's text tools: words (<c>splitWords</c>), their measures (<c>measure</c>) and line building.</summary>
internal static class PdfmakeText
{
    /// <summary><c>TextTools.buildInlines</c> for a text node: its runs split into words and measured, in order.</summary>
    public static List<Inline> Measure(IReadOnlyList<TextRun> runs, DocumentFonts fonts)
    {
        var inlines = new List<Inline>();
        foreach (var run in runs)
        {
            foreach (var (text, lineEnd) in SplitWords(run.Text))
            {
                inlines.Add(new Inline { Text = text, LineEnd = lineEnd, Font = fonts.For(run.Bold), FontSize = run.FontSize, Alignment = run.Alignment });
            }
        }

        foreach (var item in inlines)
        {
            item.Width = item.Font.WidthOfString(item.Text, item.FontSize);
            item.Height = item.Font.LineHeight(item.FontSize) * 1;
            var leading = LeadingWhitespace(item.Text);
            item.LeadingCut = 0;
            if (leading.Length > 0)
            {
                item.LeadingCut += item.Font.WidthOfString(leading, item.FontSize);
            }

            var trailing = TrailingWhitespace(item.Text);
            item.TrailingCut = trailing.Length > 0 ? item.Font.WidthOfString(trailing, item.FontSize) : 0;
        }

        return inlines;
    }

    /// <summary><c>splitWords</c>: tabs become 4 spaces; a word ends at each UAX #14 break; a required break ends the line.</summary>
    public static List<(string Text, bool LineEnd)> SplitWords(string text)
    {
        text = text.Replace("\t", "    ", StringComparison.Ordinal);
        var results = new List<(string, bool)>();
        var last = 0;
        var breaker = new LineBreaker(text);
        while (breaker.NextBreak() is { } bk)
        {
            var word = text[last..bk.Position];
            var newLine = EndsWithNewLine(word);
            if (bk.Required || newLine > 0)
            {
                results.Add((word[..^newLine], true));
            }
            else
            {
                results.Add((word, false));
            }

            last = bk.Position;
        }

        return results;
    }

    /// <summary>
    /// <c>buildNextLine</c>: takes inlines from the front of <paramref name="inlines"/> while they fit; a word wider than
    /// the line is cut by an average character width.
    /// </summary>
    public static Line? BuildNextLine(List<Inline> inlines, double availableWidth)
    {
        if (inlines.Count == 0)
        {
            return null;
        }

        var line = new Line(availableWidth);
        while (inlines.Count > 0 && line.HasEnoughSpaceForInline(inlines[0]))
        {
            var inline = inlines[0];
            inlines.RemoveAt(0);
            if (inline.Text.Length > 1 && inline.Width > line.MaxWidth)
            {
                var widthPerChar = inline.Width / inline.Text.Length;
                var maxChars = (int)Math.Floor(line.MaxWidth / widthPerChar);
                if (maxChars < 1)
                {
                    maxChars = 1;
                }

                if (maxChars < inline.Text.Length)
                {
                    var newInline = inline.Clone();
                    newInline.Text = inline.Text[maxChars..];
                    inline.Text = inline.Text[..maxChars];
                    newInline.Width = newInline.Font.WidthOfString(newInline.Text, newInline.FontSize);
                    inline.Width = inline.Font.WidthOfString(inline.Text, inline.FontSize);
                    inlines.Insert(0, newInline);
                }
            }

            line.AddInline(inline);
        }

        line.LastLineInParagraph = inlines.Count == 0;
        return line;
    }

    /// <summary><c>ElementWriter.alignLine</c> for a line laid out in <paramref name="width"/>.</summary>
    public static void AlignLine(Line line, double width)
    {
        var lineWidth = line.Width;
        var alignment = line.Inlines.Count > 0 ? line.Inlines[0].Alignment : null;
        var offset = alignment switch
        {
            "right" => width - lineWidth,
            "center" => (width - lineWidth) / 2,
            _ => 0.0,
        };

        if (offset != 0 && !double.IsNaN(offset))
        {
            line.X += offset;
        }

        if (alignment == "justify" && !line.NewLineForced && !line.LastLineInParagraph && line.Inlines.Count > 1)
        {
            var additionalSpacing = (width - lineWidth) / (line.Inlines.Count - 1);
            for (var i = 1; i < line.Inlines.Count; i++)
            {
                line.Inlines[i].X += i * additionalSpacing;
            }
        }
    }

    /// <summary>JavaScript <c>\s</c> (ES5): the characters <c>/^(\s)+/</c> and <c>/(\s)+$/</c> match.</summary>
    internal static bool IsJsWhitespace(char c) => (int)c is 0x20 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0xA0 or 0x1680
        or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    private static string LeadingWhitespace(string text)
    {
        var n = 0;
        while (n < text.Length && IsJsWhitespace(text[n]))
        {
            n++;
        }

        return text[..n];
    }

    private static string TrailingWhitespace(string text)
    {
        var n = text.Length;
        while (n > 0 && IsJsWhitespace(text[n - 1]))
        {
            n--;
        }

        return text[n..];
    }

    /// <summary>The length of <c>/\r?\n$|\r$/</c> at the end of the word (0 when it does not match).</summary>
    private static int EndsWithNewLine(string word) =>
        word.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : word.EndsWith('\n') || word.EndsWith('\r') ? 1 : 0;
}
