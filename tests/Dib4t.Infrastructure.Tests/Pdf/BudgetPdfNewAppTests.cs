using Dib4t.Application.Budgets;
using Dib4t.Infrastructure.Pdf;
using Dib4t.Infrastructure.Pdf.Layout;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Time.Testing;

namespace Dib4t.Infrastructure.Tests.Pdf;

/// <summary>
/// The new app's PDF (ADR-0005): D22 (a wrapped text pushes the elements below it in its block; the footer stays on
/// its page), D23 (the Lisbon date, set by the caller) and D25 (the footer moves to the next page when the
/// description reaches it). Budgets without long texts look exactly like the legacy PDF.
/// </summary>
public sealed class BudgetPdfNewAppTests
{
    /// <summary>The samples D22/D23/D25 change, compared against approved images (tests/fixtures/pdf-approved).</summary>
    private static readonly string[] Changed = ["02-revision", "05-most-lines", "09-utc-date-differs", "10-longest-texts"];

    private static readonly string[] UnchangedSamples = [.. PdfGolden.Layouts
        .Where(c => c.GetProperty("kind").GetString() == "sample").Select(c => c.GetProperty("id").GetString()!).Except(Changed)];

    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    public static TheoryData<string> Unchanged => new(UnchangedSamples);

    public static TheoryData<string> ChangedSamples => new(Changed);

    [Theory]
    [MemberData(nameof(Unchanged))]
    public void A_budget_without_long_texts_looks_exactly_like_the_legacy_PDF(string sample)
    {
        var actual = PdfPixels.Render(Generate(sample));
        var expected = PdfPixels.Render(File.ReadAllBytes(Fixtures.PdfSample(sample + ".pdf")));

        actual.Should().HaveSameCount(expected);
        for (var page = 0; page < expected.Count; page++)
        {
            PdfPixels.DifferentPixels(expected[page], actual[page]).Should().Be(0, $"{sample} page {page + 1}");
        }
    }

    [Theory]
    [MemberData(nameof(ChangedSamples))]
    public void The_samples_D22_and_D23_change_look_like_their_approved_images(string sample)
    {
        var actual = PdfPixels.Render(Generate(sample));
        var approved = Directory.GetFiles(Fixtures.PdfApproved, sample + "-p*.png").Order(StringComparer.Ordinal).ToList();
        for (var page = 0; page < actual.Count; page++)
        {
            PdfPixels.Save(actual[page], Path.Combine(PdfPixels.OutputDirectory, $"{sample}-p{page + 1}-approved-candidate.png"));
            if (Environment.GetEnvironmentVariable("DIB4T_PDF_APPROVE") == "1")
            {
                PdfPixels.Save(actual[page], Path.Combine(Fixtures.PdfApproved, $"{sample}-p{page + 1}.png"));
            }
        }

        approved.Should().HaveSameCount(actual, $"approved pages of {sample} (re-approve with DIB4T_PDF_APPROVE=1, see the README there)");
        var expected = PdfPixels.Load(approved);
        for (var page = 0; page < expected.Count; page++)
        {
            PdfPixels.DifferentPixels(expected[page], actual[page]).Should().Be(0, $"{sample} page {page + 1}");
        }
    }

    [Fact]
    public void A_wrapped_client_name_pushes_the_client_block_down_and_grows_the_grey_box()
    {
        var content = Sample("01-typical") with { ClientName = string.Join(' ', Enumerable.Repeat("Nome Muito Comprido", 8)) };
        var legacy = PdfsharpBudgetPdfGenerator.LegacyLayout(content)[0];
        var pages = Layout(content);
        var name = LinesAt(pages[0], 285).Where(l => l.Inlines[0].FontSize == 15).ToList();
        var extra = (name.Count - 1) * 17.578125;

        name.Should().HaveCount(5);
        LineStarting(pages[0], "Mebxfmp").Y.Should().BeApproximately(185 + extra, 1e-9, "the address moves down by the extra lines");
        LineStarting(pages[0], "ATT.:").Y.Should().BeApproximately(241 + extra, 1e-9);
        Rect(pages[0]).H.Should().BeApproximately(115 + extra, 1e-9);
        LineStarting(legacy, "Mebxfmp").Y.Should().Be(185, "legacy draws the address at its fixed place, under the name");

        // The grey box now ends at 260 + 70.3 = 330.3 > 280: everything from "Descritivo" moves down by the overflow.
        var shift = 145 + 115 + extra - 280;
        LineStarting(pages[0], "Descritivo:").Y.Should().BeApproximately(290 + shift, 1e-9);
        Separator(pages[0]).Y1.Should().BeApproximately(320 + shift, 1e-9);
        LineStarting(pages[0], "Designação").Y.Should().BeApproximately(330 + shift, 1e-9);
    }

    [Fact]
    public void A_wrapped_payment_condition_pushes_the_VAT_line_and_the_total_down()
    {
        var content = Sample("01-typical") with { PaymentMethod = string.Join(' ', Enumerable.Repeat("30 dias após receção da fatura, com 50% na adjudicação", 2)) };
        var page = Layout(content)[0];

        LinesAt(page, 30).Count(l => l.Y is > 765 and < 785).Should().Be(1, "the payment condition takes two lines");
        var vat = LineStarting(page, "Ao valor");
        // The VAT line moves down one 9 pt line; Total stays 5 pt above it and would end at 811.6, past the margin
        // (801.89): the whole footer moves up by that overflow.
        vat.Y.Should().BeApproximately(785 + 10.546875 - Overflow(785 + 10.546875 - 5 + 21.09375), 1e-9);
        LineStarting(page, "Total").Y.Should().BeApproximately(vat.Y - 5, 1e-9, "Total keeps its legacy offset to the VAT line");
    }

    [Fact]
    public void A_footer_that_would_pass_the_bottom_margin_moves_up_and_stays_on_its_page()
    {
        var content = Sample("01-typical") with { Total = 123456789012.34 };
        PdfsharpBudgetPdfGenerator.LegacyLayout(content).Should().HaveCount(3, "legacy: the total wraps past the page and the footer moves to page 2, the rest to 3");

        var pages = Layout(content);
        pages.Should().HaveCount(1);
        var total = LinesAt(pages[0], 0).Where(l => l.Inlines[0].FontSize == 18).ToList();
        total.Should().HaveCount(2);
        (total[^1].Y + total[^1].Height).Should().BeApproximately(841.89 - 40, 1e-9, "the footer moved up exactly by the overflow");
        LineStarting(pages[0], "....").Y.Should().BeApproximately(710 - (780 + (2 * 21.09375) - 801.89), 1e-9, "the whole footer moved");
    }

    [Fact]
    public void A_description_that_reaches_the_footer_puts_the_footer_on_a_new_page_D25()
    {
        // 28 lines from y = 355 end at 748.75: under the footer (710) in legacy, still on one page.
        var content = Sample("01-typical") with { Description = string.Join('\n', Enumerable.Range(1, 28).Select(i => $"Linha {i}")) };
        var legacy = PdfsharpBudgetPdfGenerator.LegacyLayout(content);
        legacy.Should().HaveCount(1);
        LinesAt(legacy[0], 30).Should().Contain(l => l.Inlines[0].Font.Name == "Roboto-Regular" && l.Y + l.Height > 710, "legacy: description under the footer");

        var pages = Layout(content);

        pages.Should().HaveCount(2);
        pages[0].Items.OfType<LineItem>().Should().NotContain(i => i.Node == "anchor" || i.Node == "footer");
        pages[1].Items.OfType<LineItem>().Should().OnlyContain(i => i.Node == "anchor" || i.Node == "footer", "the footer alone on the new page");
        LineStarting(pages[1], "....").Y.Should().Be(710, "at its usual place");
        LineStarting(pages[1], "Total").Y.Should().Be(780);
    }

    [Fact]
    public void A_footer_page_still_reached_by_the_description_passes_the_footer_on_again_D25()
    {
        // Enough lines for page 1, all of page 2 and part of page 3: legacy draws the footer on page 2, over the text.
        var content = Sample("01-typical") with { Description = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"Linha {i}")) };
        PdfsharpBudgetPdfGenerator.LegacyLayout(content)[1].Items.OfType<LineItem>().Should().Contain(i => i.Node == "anchor");

        var pages = Layout(content);

        pages.Should().HaveCount(3);
        pages[2].Items.OfType<LineItem>().Should().Contain(i => i.Node == "anchor");
        pages[2].Items.OfType<LineItem>().Where(i => i.Node == "description").Should().OnlyContain(i => i.Line.Y + i.Line.Height <= 710);
        pages[1].Items.OfType<LineItem>().Should().NotContain(i => i.Node == "anchor" || i.Node == "footer");
    }

    [Fact]
    public void Texts_that_fit_are_drawn_exactly_where_legacy_draws_them()
    {
        foreach (var sample in UnchangedSamples)
        {
            var content = Sample(sample);
            PdfGolden.Canonical(Layout(content)).Should().Be(PdfGolden.Canonical(PdfsharpBudgetPdfGenerator.LegacyLayout(content)), sample);
        }
    }

    [Fact]
    public void The_generator_writes_a_PDF_with_the_embedded_subset_fonts_and_the_text_searchable()
    {
        var pdf = new PdfsharpBudgetPdfGenerator(new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero))).Generate(Sample("01-typical"));

        var text = System.Text.Encoding.Latin1.GetString(pdf);
        text.Should().StartWith("%PDF-1.3");
        text.TrimEnd().Should().EndWith("%%EOF");
        text.Should().Contain("/Subtype/Type0").And.Contain("/CIDFontType2").And.Contain("/ToUnicode").And.Contain("/FontFile2")
            .And.Contain("+Roboto-Regular").And.Contain("+Roboto-Medium").And.Contain("/Filter/DCTDecode");
        pdf.Length.Should().BeLessThan(120_000, "only the used glyphs are embedded (legacy file: 75 KB)");
    }

    private static byte[] Generate(string sample) =>
        new PdfsharpBudgetPdfGenerator(new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero))).Generate(Sample(sample));

    /// <summary>A sample's inputs with the Lisbon date (D23), as the application gives them.</summary>
    private static BudgetPdfContent Sample(string id) => PdfGolden.Content(PdfGolden.Layout(id).GetProperty("inputs"), Lisbon);

    private static IReadOnlyList<Page> Layout(BudgetPdfContent content) => BudgetPdfDocument.Layout(content, 386, 360, growBlocks: true);

    private static double Overflow(double bottom) => Math.Max(0, bottom - (841.89 - 40));

    private static List<Line> LinesAt(Page page, double x) =>
        [.. page.Items.OfType<LineItem>().Select(i => i.Line).Where(l => x == 0 || Math.Abs(l.X - x) < 1e-9)];

    private static Line LineStarting(Page page, string text) =>
        page.Items.OfType<LineItem>().Select(i => i.Line).First(l => string.Concat(l.Inlines.Select(i => i.Text)).StartsWith(text, StringComparison.Ordinal));

    private static RectVector Rect(Page page) => page.Items.OfType<VectorItem>().Select(v => v.Vector).OfType<RectVector>().Single();

    private static LineVector Separator(Page page) => page.Items.OfType<VectorItem>().Select(v => v.Vector).OfType<LineVector>().Single(l => Math.Abs(l.LineWidth - 1) < 1e-9);
}
