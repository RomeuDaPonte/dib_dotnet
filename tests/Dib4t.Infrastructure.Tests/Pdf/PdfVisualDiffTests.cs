using Dib4t.Infrastructure.Pdf;
using Dib4t.Tests.Common;

namespace Dib4t.Infrastructure.Tests.Pdf;

/// <summary>
/// The visual diff of PLAN.md §7.1: the new generator's PDF of each reference budget, rendered with the same renderer
/// at the same resolution as the PDF the legacy app made for it (tests/fixtures/legacy/pdf), page by page.
/// </summary>
public sealed class PdfVisualDiffTests
{
    /// <summary>
    /// No pixel may differ: both files are rendered by the same PDFium in the same run, and the new file draws the same
    /// glyphs of the same fonts at the same positions with the same operators (checked 2026-11-16: 16 of 16 pages identical).
    /// </summary>
    private const double PageTolerance = 0;

    public static TheoryData<string> Samples => new(PdfGolden.Layouts.Where(c => c.GetProperty("kind").GetString() == "sample").Select(c => c.GetProperty("id").GetString()!));

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_legacy_layout_looks_like_the_PDF_of_the_legacy_app(string sample)
    {
        var inputs = PdfGolden.Layout(sample).GetProperty("inputs");
        var pdf = PdfsharpBudgetPdfGenerator.LegacyPdf(PdfGolden.Content(inputs), new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc));
        var actual = PdfPixels.Render(pdf);
        var expected = PdfPixels.Render(File.ReadAllBytes(Fixtures.PdfSample(sample + ".pdf")));

        actual.Should().HaveSameCount(expected, "page count");
        for (var page = 0; page < expected.Count; page++)
        {
            var name = $"{sample}-p{page + 1}";
            PdfPixels.Save(actual[page], Path.Combine(PdfPixels.OutputDirectory, name + "-new.png"));
            PdfPixels.Save(expected[page], Path.Combine(PdfPixels.OutputDirectory, name + "-legacy.png"));
            var different = PdfPixels.DifferentPixels(expected[page], actual[page], Path.Combine(PdfPixels.OutputDirectory, name + "-diff.png"));
            var share = (double)different / (expected[page].Width * expected[page].Height);
            share.Should().BeLessThanOrEqualTo(PageTolerance, $"{name}: {different} pixels differ (see {PdfPixels.OutputDirectory})");
        }
    }
}
