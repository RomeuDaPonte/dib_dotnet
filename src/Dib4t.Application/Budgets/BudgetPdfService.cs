using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Application.Budgets;

/// <summary>
/// <c>GET /api/budget/getPdf/:id</c> (legacy <c>routes/budget.js</c>, T as legacy; PLAN.md §6, §7.1 "PDF"): the
/// "Proposta comercial" of a budget and its <c>wasPdfDownloaded</c> flag. Legacy order: the populated budget →
/// the document → <c>pdfDownloaded()</c> + <c>save()</c> → the file. Legacy fails with a TypeError (Express 500)
/// when the budget, its client, its author or its date is missing; here a 500 <see cref="LegacyMessages.SomethingFailed"/>
/// (D16) with the reason in the log. The flag is set for any budget, also one with a job (legacy does not check).
/// D23: "Data do Documento" is the budget's date in <see cref="AppOptions.TimeZone"/>.
/// </summary>
public sealed partial class BudgetPdfService(
    IBudgetRepository budgets,
    IEntityRepository entities,
    IBudgetPdfGenerator generator,
    IOptions<AppOptions> options,
    ILogger<BudgetPdfService> logger)
{
    public async Task<Result<BudgetPdf>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (LegacyObjectId.Cast(id) is not { } objectId)
        {
            return NotPrinted(id, "not a budget id (legacy cast error)");
        }

        var budget = await budgets.FindByIdAsync(objectId, cancellationToken);
        if (budget is null)
        {
            return NotPrinted(objectId, "no budget with this id (legacy TypeError: Cannot read properties of null)");
        }

        var author = budget.MadeById is null ? null : await budgets.FindAuthorAsync(budget.MadeById, cancellationToken);
        if (author is null)
        {
            return NotPrinted(objectId, "the budget's author (madeBy) does not exist (legacy TypeError)");
        }

        var client = budget.ClientId is null ? null : await entities.FindByIdAsync(budget.ClientId, cancellationToken);
        if (client is null)
        {
            return NotPrinted(objectId, "the budget's client does not exist (legacy TypeError)");
        }

        if (budget.Date is not { } date)
        {
            return NotPrinted(objectId, "the budget has no date (legacy TypeError)");
        }

        var zone = options.Value.FindTimeZone() ?? TimeZoneInfo.Utc;
        var documentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(date, DateTimeKind.Utc), zone));
        var pdf = generator.Generate(new BudgetPdfContent(budget.BudgetNumber, budget.Revision, documentDate, author.Name, author.Email,
            client.Name, client.Address, client.Location, client.ZipCode, client.FiscalNumber, budget.PersonInCharge, budget.Descriptive,
            budget.Description, budget.DaysNeededToDoTheWork, budget.Validity, budget.PaymentMethod, budget.Total));

        if (await budgets.SetPdfDownloadedAsync(budget.Id, cancellationToken) is null)
        {
            return NotPrinted(objectId, "the budget was deleted meanwhile");
        }

        LogPrinted(budget.Id, budget.BudgetNumber, budget.Revision, pdf.Length, !budget.WasPdfDownloaded);
        return new BudgetPdf(pdf, budget.BudgetNumber, budget.Revision);
    }

    private Error NotPrinted(string id, string reason)
    {
        LogNotPrinted(id, reason);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
    }

    [LoggerMessage(EventId = 83, Level = LogLevel.Information,
        Message = "Budget {BudgetId} ({BudgetNumber} R{Revision}): PDF made, {Bytes} bytes; wasPdfDownloaded set {Changed}")]
    private partial void LogPrinted(string budgetId, string? budgetNumber, double revision, int bytes, bool changed);

    [LoggerMessage(EventId = 84, Level = LogLevel.Warning, Message = "No PDF for budget {BudgetId}: {Reason}")]
    private partial void LogNotPrinted(string budgetId, string reason);
}

/// <summary>The PDF of a budget, with what names the downloaded file.</summary>
public sealed record BudgetPdf(byte[] Pdf, string? BudgetNumber, double Revision);
