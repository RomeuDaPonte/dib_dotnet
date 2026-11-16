namespace Dib4t.Application.Budgets;

/// <summary>
/// Writes the "Proposta comercial" PDF of a budget (legacy <c>helpers/budgetPdfGenerator.js</c> + pdfmake,
/// PLAN.md §7.1 "PDF"; D22: long texts push what is below them instead of overlapping it).
/// </summary>
public interface IBudgetPdfGenerator
{
    /// <summary>The PDF file.</summary>
    byte[] Generate(BudgetPdfContent content);
}

/// <summary>
/// What the PDF prints, as the legacy template literals see it: a <c>null</c> text or number prints
/// <c>undefined</c> (JavaScript <c>String(undefined)</c>), numbers print as JavaScript <c>Number#toString</c>.
/// <see cref="DocumentDate"/> is "Data do Documento": the budget's date in the business time zone (D23).
/// </summary>
public sealed record BudgetPdfContent(
    string? BudgetNumber,
    double Revision,
    DateOnly DocumentDate,
    string? MadeByName,
    string? MadeByEmail,
    string? ClientName,
    string? ClientAddress,
    string? ClientLocation,
    string? ClientZipCode,
    string? ClientFiscalNumber,
    string? PersonInCharge,
    string? Descriptive,
    string? Description,
    double DaysNeededToDoTheWork,
    double Validity,
    string? PaymentMethod,
    double? Total);
