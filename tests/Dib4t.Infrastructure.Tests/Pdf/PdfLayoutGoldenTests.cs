using Dib4t.Infrastructure.Pdf;
using Dib4t.Infrastructure.Pdf.Fonts;
using Dib4t.Infrastructure.Pdf.Layout;
using Dib4t.Infrastructure.Pdf.Text;

namespace Dib4t.Infrastructure.Tests.Pdf;

/// <summary>
/// The .NET port of the legacy pdfmake against the legacy code itself (tools/legacy/pdf-golden.js): the UAX #14 breaks
/// of the bundled <c>linebreak</c>, fontkit's glyphs and advances for every string (both fonts, same order, fresh
/// fonts), the laid-out pages of the 11 reference samples and the synthetic budgets, and a digest of the pages of every
/// budget of the anonymized production backup. All in the legacy layout (no D22) with the legacy UTC date.
/// </summary>
public sealed class PdfLayoutGoldenTests
{
    [Fact]
    public void Line_breaks_are_those_of_the_bundled_linebreak()
    {
        var cases = PdfGolden.Load("line-breaks.json").GetProperty("cases").EnumerateArray().ToList();
        foreach (var c in cases)
        {
            var text = c.GetProperty("text").GetString()!;
            var expected = c.GetProperty("breaks").EnumerateArray().Select(b => new LineBreak(b[0].GetInt32(), b[1].GetBoolean()));
            LineBreaker.All(text).Should().Equal(expected, $"breaks of «{text}»");
        }

        cases.Should().HaveCount(77);
    }

    [Fact]
    public void Glyphs_and_advances_are_those_of_fontkit()
    {
        var fonts = new Dictionary<string, LayoutFont>(StringComparer.Ordinal)
        {
            ["Roboto-Regular"] = new(OpenTypeFont.Regular),
            ["Roboto-Medium"] = new(OpenTypeFont.Medium),
        };
        var compared = 0;
        foreach (var c in PdfGolden.Load("shaping.json").GetProperty("cases").EnumerateArray())
        {
            var font = fonts[c.GetProperty("font").GetString()!];
            if (c.TryGetProperty("metrics", out var metrics))
            {
                font.Font.UnitsPerEm.Should().Be(metrics.GetProperty("unitsPerEm").GetInt32());
                font.Ascender.Should().Be(metrics.GetProperty("ascender").GetDouble());
                font.Descender.Should().Be(metrics.GetProperty("descender").GetDouble());
                font.LineGap.Should().Be(metrics.GetProperty("lineGap").GetDouble());
                font.LineHeight(12).Should().Be(metrics.GetProperty("lineHeight12").GetDouble());
                continue;
            }

            var text = c.GetProperty("text").GetString()!;
            var shaped = font.Layout(text);
            var because = $"{font.Name} «{text}» (U+{string.Join(" U+", text.EnumerateRunes().Select(r => r.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)))})";
            shaped.Glyphs.Select(g => g.Glyph).Should().Equal(c.GetProperty("glyphs").EnumerateArray().Select(g => g.GetInt32()), because);
            shaped.Glyphs.Select(g => g.XAdvance).Should().Equal(c.GetProperty("advances").EnumerateArray().Select(g => g.GetDouble()), because);
            if (c.TryGetProperty("offsets", out var offsets))
            {
                shaped.Glyphs.Select(g => (g.XOffset, g.YOffset)).Should().Equal(offsets.EnumerateArray().Select(o => (o[0].GetDouble(), o[1].GetDouble())), because);
            }
            else
            {
                shaped.Glyphs.Should().OnlyContain(g => g.XOffset == 0 && g.YOffset == 0, because);
            }

            font.WidthOfString(text, 12).Should().Be(c.GetProperty("width12").GetDouble(), because);
            compared++;
        }

        compared.Should().BeGreaterThan(12_000);
    }

    [Fact]
    public void Pages_of_the_samples_and_the_synthetic_budgets_are_laid_out_as_the_legacy_pdfmake_does()
    {
        var compared = 0;
        foreach (var c in PdfGolden.Layouts)
        {
            var id = c.GetProperty("id").GetString()!;
            var expected = PdfGolden.Canonical(c.GetProperty("pages"));
            var actual = PdfGolden.Canonical(PdfsharpBudgetPdfGenerator.LegacyLayout(PdfGolden.Content(c.GetProperty("inputs"))));
            actual.Should().Be(expected, $"{id}: {PdfGolden.FirstDifference(expected, actual)}");
            compared++;
        }

        compared.Should().Be(46);
    }

    [Fact]
    public void Pages_of_every_budget_of_the_anonymized_backup_are_laid_out_as_the_legacy_pdfmake_does()
    {
        var expected = PdfGolden.Load("digests.json").GetProperty("budgets").EnumerateArray()
            .ToDictionary(b => b.GetProperty("id").GetString()!, b => b);
        var failures = new List<string>();
        var compared = 0;
        foreach (var (id, content) in PdfGolden.FixtureBudgets())
        {
            var pages = PdfsharpBudgetPdfGenerator.LegacyLayout(content);
            var digest = PdfGolden.Sha256(PdfGolden.Canonical(pages));
            var golden = expected[id];
            if (digest != golden.GetProperty("sha256").GetString())
            {
                failures.Add($"{id} ({content.BudgetNumber} R{content.Revision}): pages {pages.Count}/{golden.GetProperty("pages").GetInt32()}, "
                    + $"lines {pages.Sum(p => p.Items.Count(i => i is LineItem))}/{golden.GetProperty("lines").GetInt32()}");
            }

            compared++;
        }

        failures.Should().BeEmpty($"{failures.Count} of {compared} budgets differ (first: {string.Join("; ", failures.Take(5))})");
        compared.Should().Be(3397);
    }

    [Fact]
    public void Only_the_lookup_types_the_port_implements_are_used_by_the_default_features_of_the_fonts()
    {
        string[] defaultFeatures = ["rvrn", "ltra", "ltrm", "frac", "numr", "dnom", "ccmp", "locl", "rlig", "mark", "mkmk", "calt", "clig", "liga", "rclt", "curs", "kern"];
        foreach (var font in new[] { OpenTypeFont.Regular, OpenTypeFont.Medium })
        {
            foreach (var (table, supported) in new[] { (font.Gsub!, new[] { 1, 2, 3, 4, 5, 6, 7 }), (font.Gpos!, [1, 2, 7, 8, 9]) })
            {
                var used = table.Scripts.SelectMany(s => s.Script.DefaultLangSys?.FeatureIndexes ?? [])
                    .Select(i => table.Features[i]).Where(f => defaultFeatures.Contains(f.Tag))
                    .SelectMany(f => f.LookupListIndexes).Select(i => table.Lookups[i]).ToList();
                used.Should().OnlyContain(l => supported.Contains(l.LookupType), font.PostScriptName);
                used.SelectMany(l => l.SubTables).Should().NotContain(s => s is UnsupportedSubtable, font.PostScriptName);
            }
        }
    }

    [Fact]
    public void A_glyph_shared_by_two_code_points_keeps_the_code_points_of_its_first_lookup_as_in_fontkit()
    {
        // fontkit caches glyph objects per font; the shaping golden replays its strings on one font in order, so
        // this is covered there; here the cache itself: a second lookup of the same glyph keeps the first code points.
        var font = new FontkitFont(OpenTypeFont.Regular);
        font.GetGlyph(37, [65]).Should().Equal(65);
        font.GetGlyph(37, [0x391]).Should().Equal(65);
        font.GetGlyph(0).Should().BeEmpty();
    }

    [Fact]
    public void The_assets_are_the_legacy_files()
    {
        // Byte for byte the files of the legacy back-end (pdfmake/vfs_fonts.js, helpers/logoForPdf.js), see Pdf/Assets/README.md.
        Hash(PdfAssets.RobotoRegular).Should().Be(JsonDocumentHash("Roboto-Regular.ttf"));
        Hash(PdfAssets.RobotoMedium).Should().Be(JsonDocumentHash("Roboto-Medium.ttf"));
        Hash(PdfAssets.Logo).Should().Be(JsonDocumentHash("logo.jpg"));

        static string Hash(string asset) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PdfAssets.Read(asset)));
        static string JsonDocumentHash(string name) => PdfGolden.Load("assets.json").GetProperty(name).GetString()!;
    }
}
