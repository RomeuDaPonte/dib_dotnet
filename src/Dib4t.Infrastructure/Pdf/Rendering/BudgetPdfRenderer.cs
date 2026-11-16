using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dib4t.Infrastructure.Pdf.Fonts;
using Dib4t.Infrastructure.Pdf.Layout;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Dib4t.Infrastructure.Pdf.Rendering;

/// <summary>
/// Writes laid-out pages as a PDF with PDFsharp, drawing them with the operators pdfkit (legacy pdfmake's renderer)
/// writes: top-left coordinates (<c>1 0 0 -1 0 h cm</c>), lines and rectangles as paths, the logo as a DCT image, and
/// every inline as one text object with its glyphs in a <c>TJ</c> array (kerning as adjustments). Fonts are embedded
/// as Type 0 / CIDFontType2 with the used glyphs only and a ToUnicode map, so the text can be copied and searched.
/// </summary>
internal static class BudgetPdfRenderer
{
    public static byte[] Render(IReadOnlyList<Page> pages, byte[] jpeg, DateTime creationDate)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var fonts = new Dictionary<LayoutFont, FontUse>();
        var contents = pages.Select(page => BuildContent(page, fonts)).ToList();

        using var document = new PdfDocument();
        document.Version = 13; // PDF 1.3, as the legacy pdfkit file
        document.Info.Title = "Proposta comercial";
        document.Info.Creator = "Dib4t";
        document.Info.CreationDate = creationDate;

        var image = ImageXObject(document, jpeg);
        var fontObjects = fonts.ToDictionary(f => f.Value.Name, f => FontObject(document, f.Key, f.Value));
        foreach (var content in contents)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(PdfmakeLayout.PageWidth);
            page.Height = XUnit.FromPoint(PdfmakeLayout.PageHeight);
            var fontResources = new PdfDictionary(document);
            foreach (var name in content.Fonts)
            {
                fontResources.Elements.SetReference("/" + name, fontObjects[name]);
            }

            var xObjects = new PdfDictionary(document);
            xObjects.Elements.SetReference("/I1", image);
            page.Resources.Elements.SetValue("/Font", fontResources);
            page.Resources.Elements.SetValue("/XObject", xObjects);
            page.Resources.Elements.SetValue("/ProcSet", new PdfArray(document, new PdfName("/PDF"), new PdfName("/Text"), new PdfName("/ImageB"),
                new PdfName("/ImageC"), new PdfName("/ImageI")));
            var pageContent = new PdfDictionary(document);
            pageContent.CreateStream(Encoding.ASCII.GetBytes(content.Operators));
            pageContent.Stream.Zip();
            document.Internals.AddObject(pageContent);
            page.Elements.SetReference("/Contents", pageContent);
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static PageContent BuildContent(Page page, Dictionary<LayoutFont, FontUse> fonts)
    {
        var ops = new StringBuilder();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        Op(ops, $"1 0 0 -1 0 {N(PdfmakeLayout.PageHeight)} cm");
        foreach (var item in page.Items)
        {
            switch (item)
            {
                case VectorItem { Vector: LineVector line }:
                    Op(ops, $"{N(line.LineWidth)} w");
                    Op(ops, "[] 0 d");
                    Op(ops, "0 j");
                    Op(ops, "0 J");
                    Op(ops, $"{N(line.X1)} {N(line.Y1)} m");
                    Op(ops, $"{N(line.X2)} {N(line.Y2)} l");
                    Op(ops, $"{Color(line.LineColor)} RG");
                    Op(ops, "S");
                    break;
                case VectorItem { Vector: RectVector rect }:
                    Op(ops, "1 w");
                    Op(ops, "[] 0 d");
                    Op(ops, "0 j");
                    Op(ops, "0 J");
                    Op(ops, $"{N(rect.X)} {N(rect.Y)} {N(rect.W)} {N(rect.H)} re");
                    Op(ops, $"{Color(rect.Color)} rg");
                    Op(ops, "f");
                    break;
                case ImageItem image:
                    Op(ops, "q");
                    Op(ops, $"{N(image.Width)} 0 0 {N(-image.Height)} {N(image.X)} {N(image.Y + image.Height)} cm");
                    Op(ops, "/I1 Do");
                    Op(ops, "Q");
                    break;
                case LineItem { Line: var line }:
                    WriteLine(ops, line, fonts, used);
                    break;
                default:
                    break;
            }
        }

        return new PageContent(ops.ToString(), used);
    }

    /// <summary>pdfmake <c>renderLine</c> + pdfkit <c>_fragment</c>, one text object per inline.</summary>
    private static void WriteLine(StringBuilder ops, Line line, Dictionary<LayoutFont, FontUse> fonts, SortedSet<string> used)
    {
        var lineHeight = line.Height;
        var ascenderHeight = line.AscenderHeight();
        var descent = lineHeight - ascenderHeight;
        foreach (var inline in line.Inlines)
        {
            var text = inline.Text.Replace("\n", string.Empty, StringComparison.Ordinal);
            if (text.Length == 0)
            {
                continue;
            }

            var font = inline.Font;
            if (!fonts.TryGetValue(font, out var use))
            {
                fonts[font] = use = new FontUse("F" + (fonts.Count + 1).ToString(CultureInfo.InvariantCulture));
            }

            used.Add(use.Name);
            var size = inline.FontSize;
            var shiftToBaseline = lineHeight - (font.Ascender / 1000 * size) - descent;
            var x = line.X + inline.X;
            var y = PdfmakeLayout.PageHeight - (line.Y + shiftToBaseline) - (font.Ascender / 1000 * size);
            var shaped = font.Layout(text).Glyphs;
            foreach (var g in shaped)
            {
                use.Glyphs.Add(g.Glyph);
            }

            Op(ops, "0 0 0 rg");
            Op(ops, "q");
            Op(ops, $"1 0 0 -1 0 {N(PdfmakeLayout.PageHeight)} cm");
            Op(ops, "BT");
            Op(ops, $"1 0 0 1 {N(x)} {N(y)} Tm");
            Op(ops, $"/{use.Name} {N(size)} Tf");

            var scale = size / 1000;
            var commands = new List<string>();
            var last = 0;
            var hadOffset = false;
            void AddSegment(int cur)
            {
                if (last < cur)
                {
                    var hex = string.Concat(shaped.Skip(last).Take(cur - last).Select(g => g.Glyph.ToString("x4", CultureInfo.InvariantCulture)));
                    var advance = shaped[cur - 1].XAdvance - shaped[cur - 1].AdvanceWidth;
                    commands.Add($"<{hex}> {N(-advance)}");
                }

                last = cur;
            }

            void Flush(int i)
            {
                AddSegment(i);
                if (commands.Count > 0)
                {
                    Op(ops, $"[{string.Join(' ', commands)}] TJ");
                    commands.Clear();
                }
            }

            var index = 0;
            for (; index < shaped.Count; index++)
            {
                var pos = shaped[index];
                if (pos.XOffset != 0 || pos.YOffset != 0)
                {
                    Flush(index);
                    Op(ops, $"1 0 0 1 {N(x + (pos.XOffset * scale))} {N(y + (pos.YOffset * scale))} Tm");
                    Flush(index + 1);
                    hadOffset = true;
                }
                else
                {
                    if (hadOffset)
                    {
                        Op(ops, $"1 0 0 1 {N(x)} {N(y)} Tm");
                        hadOffset = false;
                    }

                    if (pos.XAdvance - pos.AdvanceWidth != 0)
                    {
                        AddSegment(index + 1);
                    }
                }

                x += pos.XAdvance * scale;
            }

            Flush(index);
            Op(ops, "ET");
            Op(ops, "Q");
        }
    }

    private static PdfDictionary FontObject(PdfDocument document, LayoutFont layoutFont, FontUse use)
    {
        var font = layoutFont.Font;
        var baseFont = "/" + SubsetTag(font.PostScriptName, use.Glyphs) + "+" + font.PostScriptName;
        var scale = layoutFont.Scale;

        var fontFile = new PdfDictionary(document);
        fontFile.CreateStream(TrueTypeSubset.Create(font, use.Glyphs));
        fontFile.Stream.Zip();
        document.Internals.AddObject(fontFile);

        var bbox = font.BoundingBox;
        var descriptor = new PdfDictionary(document);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", baseFont);
        descriptor.Elements.SetInteger("/Flags", 32 | (font.IsFixedPitch ? 1 : 0) | (font.ItalicAngle != 0 ? 64 : 0));
        descriptor.Elements.SetValue("/FontBBox", new PdfArray(document, new PdfReal(bbox.XMin * scale), new PdfReal(bbox.YMin * scale),
            new PdfReal(bbox.XMax * scale), new PdfReal(bbox.YMax * scale)));
        descriptor.Elements.SetReal("/ItalicAngle", font.ItalicAngle);
        descriptor.Elements.SetReal("/Ascent", layoutFont.Ascender);
        descriptor.Elements.SetReal("/Descent", layoutFont.Descender);
        descriptor.Elements.SetReal("/CapHeight", (font.CapHeight ?? font.Ascent) * scale);
        descriptor.Elements.SetReal("/XHeight", (font.XHeight ?? 0) * scale);
        descriptor.Elements.SetInteger("/StemV", 0);
        descriptor.Elements.SetReference("/FontFile2", fontFile);
        document.Internals.AddObject(descriptor);

        // pdfkit seeds its widths with this.font.getGlyph(0).advanceWidth in font units (not scaled to 1000/em like
        // every other glyph): a missing glyph's box advances by that, pushing the rest of its word to the right.
        var widths = new PdfArray(document);
        foreach (var glyph in use.Glyphs)
        {
            widths.Elements.Add(new PdfInteger(glyph));
            widths.Elements.Add(new PdfArray(document, new PdfReal(glyph == 0 ? font.AdvanceWidth(0) : font.AdvanceWidth(glyph) * scale)));
        }

        var systemInfo = new PdfDictionary(document);
        systemInfo.Elements.SetString("/Registry", "Adobe");
        systemInfo.Elements.SetString("/Ordering", "Identity");
        systemInfo.Elements.SetInteger("/Supplement", 0);

        var cidFont = new PdfDictionary(document);
        cidFont.Elements.SetName("/Type", "/Font");
        cidFont.Elements.SetName("/Subtype", "/CIDFontType2");
        cidFont.Elements.SetName("/BaseFont", baseFont);
        cidFont.Elements.SetValue("/CIDSystemInfo", systemInfo);
        cidFont.Elements.SetReference("/FontDescriptor", descriptor);
        cidFont.Elements.SetValue("/W", widths);
        cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        document.Internals.AddObject(cidFont);

        var toUnicode = new PdfDictionary(document);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(ToUnicodeMap(layoutFont, use.Glyphs)));
        toUnicode.Stream.Zip();
        document.Internals.AddObject(toUnicode);

        var type0 = new PdfDictionary(document);
        type0.Elements.SetName("/Type", "/Font");
        type0.Elements.SetName("/Subtype", "/Type0");
        type0.Elements.SetName("/BaseFont", baseFont);
        type0.Elements.SetName("/Encoding", "/Identity-H");
        type0.Elements.SetValue("/DescendantFonts", new PdfArray(document, PdfInternals.GetReference(cidFont) ?? throw new InvalidOperationException("CID font is not an indirect object.")));
        type0.Elements.SetReference("/ToUnicode", toUnicode);
        document.Internals.AddObject(type0);
        return type0;
    }

    private static string ToUnicodeMap(LayoutFont font, SortedSet<int> glyphs)
    {
        var map = new StringBuilder();
        map.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        map.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000><ffff>\nendcodespacerange\n");
        var entries = glyphs.Select(g => (Glyph: g, Text: string.Concat(font.CodePointsOf(g).Select(cp => cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(cp) : string.Empty)))).Where(e => e.Text.Length > 0).ToList();
        foreach (var chunk in entries.Chunk(100))
        {
            map.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var (glyph, text) in chunk)
            {
                var utf16 = string.Concat(text.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture)));
                map.Append(CultureInfo.InvariantCulture, $"<{glyph:x4}> <{utf16}>\n");
            }

            map.Append("endbfchar\n");
        }

        map.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return map.ToString();
    }

    private static PdfDictionary ImageXObject(PdfDocument document, byte[] jpeg)
    {
        var info = JpegInfo.Read(jpeg);
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", info.Width);
        image.Elements.SetInteger("/Height", info.Height);
        image.Elements.SetInteger("/BitsPerComponent", info.BitsPerComponent);
        image.Elements.SetName("/ColorSpace", info.Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" });
        if (info.Components == 4)
        {
            image.Elements.SetValue("/Decode", new PdfArray(document, [.. Enumerable.Range(0, 8).Select(i => (PdfItem)new PdfInteger(i % 2 == 0 ? 1 : 0))]));
        }

        image.Elements.SetName("/Filter", "/DCTDecode");
        image.CreateStream(jpeg);
        document.Internals.AddObject(image);
        return image;
    }

    /// <summary>A deterministic 6-letter subset tag (PDF 32000 9.6.4) for the font and its glyphs.</summary>
    private static string SubsetTag(string name, SortedSet<int> glyphs)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name + ":" + string.Join(',', glyphs)));
        return string.Concat(hash.Take(6).Select(b => (char)('A' + (b % 26))));
    }

    private static void Op(StringBuilder ops, string op) => ops.Append(op).Append('\n');

    /// <summary>pdfkit <c>number()</c>: rounded to 6 decimals (<c>Math.round</c>), written as JavaScript writes numbers.</summary>
    private static string N(double value) => SharedKernel.JsNumber.ToString(Math.Floor((value * 1e6) + 0.5) / 1e6);

    private static string Color(string color) => color switch
    {
        "black" => "0 0 0",
        ['#', ..] when color.Length == 7 => string.Join(' ', Enumerable.Range(0, 3).Select(i =>
            N(int.Parse(color.AsSpan(1 + (i * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0))),
        _ => "0 0 0",
    };

    private sealed record PageContent(string Operators, SortedSet<string> Fonts);

    private sealed class FontUse(string name)
    {
        public string Name { get; } = name;

        public SortedSet<int> Glyphs { get; } = [];
    }
}

/// <summary>The size and colour components of a baseline or progressive JPEG (its SOF segment).</summary>
internal readonly record struct JpegInfo(int Width, int Height, int Components, int BitsPerComponent)
{
    public static JpegInfo Read(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        var p = 2;
        while (p + 9 < jpeg.Length)
        {
            if (jpeg[p] != 0xFF)
            {
                p++;
                continue;
            }

            var marker = jpeg[p + 1];
            var length = (jpeg[p + 2] << 8) | jpeg[p + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return new JpegInfo((jpeg[p + 7] << 8) | jpeg[p + 8], (jpeg[p + 5] << 8) | jpeg[p + 6], jpeg[p + 9], jpeg[p + 4]);
            }

            p += 2 + length;
        }

        throw new InvalidDataException("Not a JPEG image (no SOF segment).");
    }
}
