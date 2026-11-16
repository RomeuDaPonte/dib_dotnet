using Dib4t.Application.Budgets;
using Dib4t.Infrastructure.Pdf.Layout;
using Dib4t.Infrastructure.Pdf.Rendering;

namespace Dib4t.Infrastructure.Pdf;

/// <summary>
/// The budget PDF (PLAN.md §7.1 "PDF"): the legacy pdfmake layout ported to .NET (fonts, shaping, line breaking and
/// positions as legacy computes them, checked against the legacy code in <c>tests/fixtures/legacy/pdf-layout</c>),
/// with D22 (long texts push what is below them), written with PDFsharp.
/// </summary>
internal sealed class PdfsharpBudgetPdfGenerator(TimeProvider time) : IBudgetPdfGenerator
{
    private static readonly Lazy<(byte[] Jpeg, JpegInfo Info)> LogoImage = new(() =>
    {
        var jpeg = PdfAssets.Read(PdfAssets.Logo);
        return (jpeg, JpegInfo.Read(jpeg));
    });

    public byte[] Generate(BudgetPdfContent content)
    {
        var (jpeg, info) = LogoImage.Value;
        var pages = BudgetPdfDocument.Layout(content, info.Width, info.Height, growBlocks: true);
        return BudgetPdfRenderer.Render(pages, jpeg, time.GetUtcNow().UtcDateTime);
    }

    /// <summary>The legacy layout (no D22), for the comparison with the legacy PDFs.</summary>
    internal static IReadOnlyList<Page> LegacyLayout(BudgetPdfContent content)
    {
        var info = LogoImage.Value.Info;
        return BudgetPdfDocument.Layout(content, info.Width, info.Height, growBlocks: false);
    }

    /// <summary>A PDF of the legacy layout (no D22), for the visual comparison with the legacy PDFs.</summary>
    internal static byte[] LegacyPdf(BudgetPdfContent content, DateTime creationDate) =>
        BudgetPdfRenderer.Render(LegacyLayout(content), LogoImage.Value.Jpeg, creationDate);
}
