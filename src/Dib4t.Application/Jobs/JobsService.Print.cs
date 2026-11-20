using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Printing;
using Dib4t.Domain.Entities;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Jobs;

/// <summary>The "Imprimir etiquetas" dialog's route (legacy <c>POST print/:budgetId</c>, PLAN.md §5, §6).</summary>
public sealed partial class JobsService
{
    /// <summary>
    /// <c>POST print/:budgetId</c>, in the legacy order (checked against <c>tests/fixtures/legacy/jobs/job-print-golden.json</c>):
    /// not Production (400 <see cref="JobMessages.PrintOnlyInProduction"/>, before anything else; legacy: <c>NODE_ENV</c>
    /// not <c>"prod"</c>) → the route id (400) → the body (400) → both templates <c>false</c> (400
    /// <see cref="JobMessages.ChooseTemplate"/>) → the budget (400 <see cref="JobMessages.BudgetMissing"/>) → its job (400
    /// <see cref="JobMessages.NoJobToPrint"/>) → if <c>producao</c>: its printers (none: 400
    /// <see cref="JobMessages.NoProductionPrinter"/>), a label on each → if <c>maquinacao</c>: the same (400
    /// <see cref="JobMessages.NoMachiningPrinter"/>, after the <c>producao</c> labels were sent, as legacy) → "ok".
    /// Nothing is written. A printer that fails is logged and the others still get their label (legacy ignores the
    /// result). What legacy throws on before the first label (the budget's client is gone, the job's <c>startDate</c> is
    /// stored <c>null</c>) is the 500 (D16). The label date is the Europe/Lisbon date of the job's start (D23); a missing
    /// <c>startDate</c> is now (Mongoose's default when the job is read). <c>LabelPrinter:Enabled=false</c> logs the
    /// labels instead of sending them (§4.7), with the same answers.
    /// </summary>
    public async Task<Result<string>> PrintLabelsAsync(string budgetId, JsonElement body, bool isProduction, string? userName,
        CancellationToken cancellationToken)
    {
        if (!isProduction)
        {
            return Error.Validation(JobMessages.PrintOnlyInProduction);
        }

        var id = JobValidators.ValidateId("budgetId", budgetId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var selection = JobValidators.ValidatePrint(body);
        if (!selection.IsSuccess)
        {
            return selection.Error;
        }

        if (selection.Value.NoneChosen)
        {
            return Error.Validation(JobMessages.ChooseTemplate);
        }

        var budget = await budgets.FindByIdAsync(id.Value, cancellationToken);
        if (budget is null)
        {
            return Error.Validation(JobMessages.BudgetMissing);
        }

        var job = await jobs.FindForLabelsAsync(id.Value, cancellationToken);
        if (job is null)
        {
            return Error.Validation(JobMessages.NoJobToPrint);
        }

        var client = budget.ClientId is null ? null : await entities.FindByIdAsync(budget.ClientId, cancellationToken);
        var sent = 0;
        foreach (var (template, chosen, none) in new[]
                 {
                     (PrintTemplates.Producao, selection.Value.Production, JobMessages.NoProductionPrinter),
                     (PrintTemplates.Maquinacao, selection.Value.Machining, JobMessages.NoMachiningPrinter),
                 })
        {
            if (!chosen)
            {
                continue;
            }

            var list = await printers.ListByTemplateAsync(template, cancellationToken);
            if (list.Count == 0)
            {
                LogNoPrinter(budget.Id, budget.BudgetNumber, template, sent);
                return Error.Validation(none);
            }

            if (LabelRefusal(client, job) is { } refusal)
            {
                LogLabelsNotPrinted(budget.Id, budget.BudgetNumber, refusal);
                return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = refusal };
            }

            var payloadDate = LabelDate(job.StartDate.HasValue ? job.StartDate.Value : Now());
            foreach (var printer in list)
            {
                await labels.PrintAsync(printer, LabelTemplates.Payload(template, budget.BudgetNumber, budget.Descriptive, client!.Name, payloadDate), cancellationToken);
                sent++;
            }
        }

        LogLabelsPrinted(budget.Id, budget.BudgetNumber, job.Id, selection.Value.Production, selection.Value.Machining, sent, userName);
        return JobMessages.Printed;
    }

    /// <summary>What legacy <c>getTemplateData</c> throws on (a TypeError before the label is sent), or <c>null</c>.</summary>
    private static string? LabelRefusal(BusinessEntity? client, JobForLabels job) =>
        job.StartDate.IsNull ? $"the job {job.Id} has startDate null (legacy TypeError: reading 'getDate' of null), no label sent"
        : client is null ? "the budget's client is not in the entities (legacy TypeError: reading 'name' of null), no label sent"
        : null;

    /// <summary>The label date: the day of <paramref name="utc"/> in the company's zone (D23; legacy: the server's local date).</summary>
    private DateOnly LabelDate(DateTime utc)
    {
        var zone = options.Value.FindTimeZone() ?? TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
    }

    [LoggerMessage(EventId = 170, Level = LogLevel.Information,
        Message = "Labels of budget {BudgetId} ({BudgetNumber}), job {JobId}, printed on request (producao {Production}, maquinacao {Machining}): {Count} sent by {UserName}")]
    private partial void LogLabelsPrinted(string budgetId, string? budgetNumber, string jobId, bool production, bool machining, int count, string? userName);

    [LoggerMessage(EventId = 171, Level = LogLevel.Warning,
        Message = "Labels of budget {BudgetId} ({BudgetNumber}): no printer with the template {Template} ({Sent} labels sent before)")]
    private partial void LogNoPrinter(string budgetId, string? budgetNumber, string template, int sent);

    [LoggerMessage(EventId = 172, Level = LogLevel.Warning, Message = "Labels of budget {BudgetId} ({BudgetNumber}) not printed (legacy 500): {Reason}")]
    private partial void LogLabelsNotPrinted(string budgetId, string? budgetNumber, string reason);
}
