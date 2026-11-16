using System.Globalization;
using Dib4t.Application.Budgets;
using Dib4t.SharedKernel;

namespace Dib4t.Infrastructure.Pdf.Layout;

/// <summary>
/// The document definition of legacy <c>helpers/budgetPdfGenerator.js</c> (<c>buildPdf</c>): every element at the
/// legacy absolute position, the same texts. With <c>growBlocks</c> (D22, ADR-0005) a text that wraps pushes the
/// elements below it in its block instead of overlapping them; texts that fit stay exactly where legacy draws them.
/// </summary>
internal static class BudgetPdfDocument
{
    private const string AnchorId = "anchor";
    private const string DottedLine =
        "................................................................................................................................................................................................................";

    /// <summary>"Descritivo" sits at y = 290; the header blocks may grow down to 10 pt above it (D22).</summary>
    private const double HeaderLimit = 280;

    /// <summary>Lays the budget's PDF out (pdfmake's pages).</summary>
    public static IReadOnlyList<Page> Layout(BudgetPdfContent budget, int logoPixelWidth, int logoPixelHeight, bool growBlocks)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var content = Build(budget, logoPixelWidth, logoPixelHeight, growBlocks ? Measure : null);
        return PdfmakeLayout.Layout(content, new DocumentFonts());

        // D22 measures every text first, on fonts of their own: the legacy content order, so the same fontkit history.
        static Func<TextNode, (int, double)> Measure()
        {
            var fonts = new DocumentFonts();
            return node => PdfmakeLayout.Measure(node, fonts);
        }
    }

    private static List<DocNode> Build(BudgetPdfContent b, int logoWidth, int logoHeight, Func<Func<TextNode, (int Lines, double LineHeight)>>? measure)
    {
        var company = new[]
        {
            Bold(30, 125, "DIB4T LDA", 15),
            Bold(30, 145, "Rua da Indústria Metalurgica nº123 B", 7),
            Bold(30, 158, "Pedrulheira", 7),
            Bold(30, 171, "2430-528 Marinha Grande", 7),
            Bold(30, 184, "Portugal", 7),
            Bold(30, 197, "Contribuinte: 514509376", 7),
            Bold(30, 210, "geral@dib4t.com", 7),
            Bold(30, 223, "www.dib4t.com", 7),
            Bold(30, 236, $"Data do Documento: {b.DocumentDate.Day.ToString(CultureInfo.InvariantCulture)}/{b.DocumentDate.Month.ToString(CultureInfo.InvariantCulture)}/{b.DocumentDate.Year.ToString(CultureInfo.InvariantCulture)}", 7),
            Bold(30, 249, $"Doc emitido por: {Js(b.MadeByName)}", 7),
            Bold(30, 262, $"Email: {Js(b.MadeByEmail)}", 7),
        };
        var client = new[]
        {
            Bold(285, 155, Js(b.ClientName), 15),
            Bold(285, 185, Js(b.ClientAddress), 9),
            Bold(285, 198, $"{Js(b.ClientLocation)}    {Js(b.ClientZipCode)}", 9),
            Bold(285, 211, $"NIF.: {Js(b.ClientFiscalNumber)}", 9),
            Bold(285, 241, $"ATT.: {Js(b.PersonInCharge)}", 9),
        };
        var descriptive = Bold(30, 290, $"Descritivo: {Js(b.Descriptive)}", 12);
        var technical = Bold(30, 330, "Designação técnica:", 12);
        var description = new TextNode(30, 355, [new TextRun(b.Description ?? string.Empty, 12, false, "justify")]);
        var footer = new[]
        {
            Bold(30, 710, DottedLine, 9) with { Id = AnchorId },
            Bold(30, 725, DaysOrWeeks(b.DaysNeededToDoTheWork), 9),
            Bold(30, 745, $"Validade da proposta: {JsNumber.ToString(b.Validity)} dias", 9),
            Bold(30, 765, $"Condições de pagamento: {Js(b.PaymentMethod)} após emissão da fatura.", 9),
            Bold(30, 785, "Ao valor apresentado acresce iva à taxa legal em vigor.", 9),
        };
        var totalLabel = Bold(380, 780, "Total", 15);
        var totalValue = new TextNode(450, 780, [new TextRun($"{(b.Total is { } total ? JsNumber.ToString(total) : "undefined")} €", 18, true, "right")]);
        var rectHeight = 115.0;
        var separatorY = 280.0;

        if (measure?.Invoke() is { } lines)
        {
            // D22, ADR-0005. The measures follow the legacy content order (title, number, company, client, …).
            lines(Title());
            lines(Number(b));
            var companyBottom = Grow(company, lines);
            Grow(client, lines);
            rectHeight += Extra(client, lines); // the grey box grows with the client block
            var shift = Math.Max(0, Math.Max(145 + rectHeight, companyBottom) - HeaderLimit);
            descriptive = descriptive with { Y = descriptive.Y + shift };
            var descriptiveExtra = Extra([descriptive], lines);
            separatorY += shift + descriptiveExtra;
            technical = technical with { Y = technical.Y + shift + descriptiveExtra };
            description = description with { Y = description.Y + shift + descriptiveExtra };
            lines(technical);
            lines(description);

            Grow(footer, lines);
            totalLabel = totalLabel with { Y = footer[^1].Y - 5 };
            totalValue = totalValue with { Y = footer[^1].Y - 5 };
            var footerBottom = Math.Max(Bottom(footer[^1], lines), Math.Max(Bottom(totalLabel, lines), Bottom(totalValue, lines)));
            var overflow = footerBottom - (PdfmakeLayout.PageHeight - PdfmakeLayout.Margin);
            if (overflow > 0)
            {
                for (var i = 0; i < footer.Length; i++)
                {
                    footer[i] = footer[i] with { Y = footer[i].Y - overflow };
                }

                totalLabel = totalLabel with { Y = totalLabel.Y - overflow };
                totalValue = totalValue with { Y = totalValue.Y - overflow };
            }
        }

        var content = new List<DocNode>
        {
            new ImageNode(30, 10, 80, logoWidth, logoHeight),
            Title(),
            new CanvasNode(
            [
                new LineVector(-20, 55, 540, 55, 2, "black"),
                new LineVector(-20, separatorY, 540, separatorY, 1, "black"),
                new RectVector(230, 105, 290, rectHeight, "#eff0f2"),
            ]),
            Number(b),
        };
        content.AddRange(company);
        content.AddRange(client);
        content.Add(descriptive);
        content.Add(technical);
        content.Add(description);
        content.AddRange(footer);
        content.Add(totalLabel);
        content.Add(totalValue);
        return content;
    }

    private static TextNode Title() => new(424, 70, [new TextRun("Proposta comercial", 15, true)]);

    private static TextNode Number(BudgetPdfContent b) =>
        new(460, 100, [new TextRun($"Nº {Js(b.BudgetNumber)} {(b.Revision > 0 ? "| R " + JsNumber.ToString(b.Revision) : string.Empty)}", 12, true)]);

    private static TextNode Bold(double x, double y, string text, double size) => new(x, y, [new TextRun(text, size, true)]);

    /// <summary><c>showDaysOrWeeksNeededToDoTheWork</c>.</summary>
    private static string DaysOrWeeks(double days) => days > 7
        ? $"Prazo de entrega: {JsNumber.ToString(Math.Ceiling(days / 7))} semanas após validação."
        : $"Prazo de entrega: {JsNumber.ToString(days)} dias após validação.";

    /// <summary>JavaScript <c>`${value}`</c> of a text: <c>undefined</c> when missing.</summary>
    private static string Js(string? value) => value ?? "undefined";

    /// <summary>Moves each element of the block down by the extra height of the wrapped elements above it; the block's bottom.</summary>
    private static double Grow(TextNode[] block, Func<TextNode, (int Lines, double LineHeight)> lines)
    {
        var extra = 0.0;
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = block[i] with { Y = block[i].Y + extra };
            var (count, height) = lines(block[i]);
            extra += Math.Max(0, count - 1) * height;
        }

        return Bottom(block[^1], lines);
    }

    /// <summary>The total extra height of the (already moved) block: what its wrapped lines add.</summary>
    private static double Extra(TextNode[] block, Func<TextNode, (int Lines, double LineHeight)> lines) =>
        block.Sum(node => lines(node) is var (count, height) ? Math.Max(0, count - 1) * height : 0);

    private static double Bottom(TextNode node, Func<TextNode, (int Lines, double LineHeight)> lines) =>
        lines(node) is var (count, height) ? node.Y + (Math.Max(1, count) * height) : node.Y;
}
