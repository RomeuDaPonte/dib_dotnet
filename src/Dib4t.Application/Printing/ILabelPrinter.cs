using System.Globalization;
using Dib4t.Domain.Settings;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Printing;

/// <summary>
/// The label printers (legacy <c>PrinterSettings.print</c>, <c>models/printerSettings.js</c>, PLAN.md §5). In
/// Production with <c>LabelPrinter:Enabled</c> a payload goes over raw TCP to the printer; everywhere else it is only
/// logged (§4.7). Never throws: like legacy, a failure is logged (category <c>Printer</c>) and reported as <c>false</c>,
/// and every caller goes on (legacy ignores the result).
/// </summary>
public interface ILabelPrinter
{
    /// <summary><c>false</c> when the labels are only logged (not Production, or <c>LabelPrinter:Enabled=false</c>).</summary>
    bool IsEnabled { get; }

    /// <summary>Sends <paramref name="payload"/> to <paramref name="printer"/> (its <c>ip</c> and <c>port</c>).</summary>
    Task<bool> PrintAsync(PrinterSettings printer, string payload, CancellationToken cancellationToken);
}

/// <summary>
/// The label templates of legacy <c>getTemplateData</c> (checked against the legacy code:
/// <c>tests/fixtures/legacy/jobs/job-golden.json</c> "labels"). Both start with ESC (U+001B), <c>ESC i a 3</c>, which
/// switches the printer to template mode; the <c>producao</c> one keeps the source's line break and indentation
/// around it. The values are written as they are (a comma inside a name is not escaped, as legacy).
/// </summary>
public static class LabelTemplates
{
    private const string Escape = "\u001b";

    /// <summary>
    /// The payload of <paramref name="templateName"/> (<see cref="PrintTemplates"/>; any other name gives <c>""</c>, as
    /// legacy). <paramref name="startDate"/> is the job's start date in the company's zone (legacy: the server's local
    /// date; D23 decided the Europe/Lisbon date for printed documents), written <c>d/M/yyyy</c> without padding.
    /// </summary>
    public static string Payload(string? templateName, string? budgetNumber, string? descriptive, string? clientName, DateOnly startDate)
    {
        var date = string.Create(CultureInfo.InvariantCulture, $"{startDate.Day}/{startDate.Month}/{startDate.Year}");
        var (number, title, client) = (Js(budgetNumber), Js(descriptive), Js(clientName));
        return templateName switch
        {
            PrintTemplates.Producao => $"\n      {Escape}ia3,^TS001{number},{date},{title},{client},{number},\n      ",
            PrintTemplates.Maquinacao => $"{Escape}ia3,^TS002{number},{date},{title},{client},",
            _ => string.Empty,
        };
    }

    /// <summary>A JavaScript template literal's text of a missing value.</summary>
    private static string Js(string? value) => value ?? "undefined";
}
