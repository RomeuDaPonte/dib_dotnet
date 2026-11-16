using System.Globalization;

namespace Dib4t.Infrastructure.Pdf.Text;

/// <summary>
/// Per code point Unicode data exactly as the legacy pdfmake 0.1.37 bundle has it (<c>Assets/unicode-ranges.txt</c>,
/// written by <c>tools/legacy/pdf-golden.js</c> from the bundle's own tables): the UAX #14 class of its
/// <c>linebreak</c> trie, the general category of its <c>unicode-properties</c> and the OpenType script tag fontkit
/// derives from it. The legacy tables are older than .NET's Unicode data, so they are not taken from the BCL.
/// </summary>
internal static class UnicodeData
{
    /// <summary>The <c>linebreak</c> class constants, by value (<c>classes.js</c> of the bundle).</summary>
    internal static readonly string[] LineBreakClassNames =
    [
        "OP", "CL", "CP", "QU", "GL", "NS", "EX", "SY", "IS", "PR", "PO", "NU", "AL", "HL", "ID", "IN", "HY", "BA", "BB", "B2",
        "ZW", "CM", "WJ", "H2", "H3", "JL", "JV", "JT", "RI", "AI", "BK", "CB", "CJ", "CR", "LF", "NL", "SA", "SG", "SP", "XX",
    ];

    private static readonly Lazy<Table> Data = new(Load);

    /// <summary>The UAX #14 class (a <see cref="LineBreakClassNames"/> index) of the bundled trie.</summary>
    public static int LineBreakClass(int codePoint) => Data.Value.Find(codePoint).LineBreak;

    /// <summary><c>unicode.isMark</c>: general category Mn, Me or Mc.</summary>
    public static bool IsMark(int codePoint) => Data.Value.Find(codePoint).Category is "Mn" or "Me" or "Mc";

    /// <summary><c>unicode.isDigit</c>: general category Nd.</summary>
    public static bool IsDigit(int codePoint) => Data.Value.Find(codePoint).Category == "Nd";

    /// <summary>A script fontkit has no OpenType tag for: its <c>forString</c> stops there and answers <c>undefined</c>.</summary>
    public static readonly IReadOnlyList<string> UntaggedScript = ["?"];

    /// <summary>
    /// The OpenType script tag(s) fontkit gives the code point's script (<c>UNICODE_SCRIPTS</c>), <c>null</c> for
    /// Common, Inherited and Unknown (the scripts fontkit's <c>forString</c> skips), or <see cref="UntaggedScript"/>.
    /// </summary>
    public static IReadOnlyList<string>? Script(int codePoint) => Data.Value.Find(codePoint).Script;

    private static Table Load()
    {
        using var stream = PdfAssets.Open("unicode-ranges.txt");
        using var reader = new StreamReader(stream);
        var starts = new List<int>();
        var entries = new List<Entry>();
        var classIndex = LineBreakClassNames.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.Ordinal);
        var scripts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["?"] = UntaggedScript };
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split(' ');
            var start = int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var lineBreak = classIndex.TryGetValue(parts[2], out var c) ? c : int.Parse(parts[2], CultureInfo.InvariantCulture);
            var category = categories.TryGetValue(parts[3], out var known) ? known : categories[parts[3]] = parts[3];
            IReadOnlyList<string>? script = null;
            if (parts[4] != "-" && !scripts.TryGetValue(parts[4], out script))
            {
                scripts[parts[4]] = script = parts[4].Split(',');
            }

            starts.Add(start);
            entries.Add(new Entry(lineBreak, category, script));
        }

        return new Table([.. starts], [.. entries]);
    }

    private sealed record Entry(int LineBreak, string Category, IReadOnlyList<string>? Script);

    private sealed class Table(int[] starts, Entry[] entries)
    {
        public Entry Find(int codePoint)
        {
            var i = Array.BinarySearch(starts, codePoint);
            if (i < 0)
            {
                i = ~i - 1;
            }

            // Code points outside 0..10FFFF (JavaScript NaN from charCodeAt past the end) read the first range, as
            // the trie's error value does not matter there: the callers never use it.
            return entries[Math.Clamp(i, 0, entries.Length - 1)];
        }
    }
}
