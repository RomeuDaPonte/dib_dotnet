using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Application.Nas;
using Dib4t.Application.Printing;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Application.Jobs;

/// <summary>
/// The job routes of legacy <c>routes/job.js</c> (every route T: any signed-in user, as legacy; PLAN.md §6, §7.3).
/// This step: create. Same order of checks and texts as the legacy route; a failure legacy leaves to Express is a 500
/// <see cref="LegacyMessages.SomethingFailed"/> (D16) with the reason in the log only.
/// </summary>
public sealed partial class JobsService(
    IBudgetRepository budgets,
    IBudgetTaskRepository tasks,
    IEntityRepository entities,
    IJobRepository jobs,
    IPrinterSettingsRepository printers,
    INasFileShare nas,
    ILabelPrinter labels,
    TimeProvider time,
    IOptions<AppOptions> options,
    ILogger<JobsService> logger)
{
    /// <summary>
    /// <c>POST /</c>: Joi (400) → the budget (400 <see cref="JobMessages.BudgetMissing"/>) → no revision of its
    /// number has a job (400 <see cref="JobMessages.AlreadyCreated"/>) → its number has a revision 0 (400
    /// <see cref="JobMessages.RevisionZeroMissing"/>) → its tasks → the checks of the legacy saves (the budget's
    /// required fields; <b>D26</b>: a value Mongoose would refuse in the job is refused here, before anything is
    /// written; legacy marks the budget first and then fails, so the number could never get its job) → the budget's
    /// <c>hasGenerateJob</c> → the job (zero costs, budgeted hours per type, priority "Média", <c>createdBy</c> = the
    /// signed-in user's name, start now) → NAS: the budget folder's documents and geometry into the job folder (§5;
    /// D3/D19: from the folder of revision 0, where legacy created it; D4: the check is real and only logged) → the
    /// labels: every <c>producao</c> printer, then every <c>maquinacao</c> printer (failures logged, ignored as legacy).
    /// A failed NAS command is the legacy 500 after the job is saved, without labels.
    /// </summary>
    public async Task<Result<JobCreated>> CreateAsync(JsonElement body, string? userName, CancellationToken cancellationToken)
    {
        var validated = JobValidators.ValidateNew(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var input = validated.Value;
        var budget = await budgets.FindByIdAsync(input.BudgetId, cancellationToken);
        if (budget is null)
        {
            return Error.Validation(JobMessages.BudgetMissing);
        }

        if (await budgets.FindWithJobAsync(budget.BudgetNumber, cancellationToken) is not null)
        {
            LogAlreadyCreated(budget.Id, budget.BudgetNumber);
            return Error.Validation(JobMessages.AlreadyCreated);
        }

        var revisionZero = await budgets.FindRevisionZeroAsync(budget.BudgetNumber, cancellationToken);
        if (revisionZero is null)
        {
            return Error.Validation(JobMessages.RevisionZeroMissing);
        }

        var budgetTasks = await tasks.ListByBudgetAsync(budget.Id, cancellationToken);
        var client = budget.ClientId is null ? null : await entities.FindByIdAsync(budget.ClientId, cancellationToken);
        var author = budget.MadeById is null ? null : await budgets.FindAuthorAsync(budget.MadeById, cancellationToken);
        if (BudgetSaveRefusal(budget, client, author) is { } budgetRefusal)
        {
            return NotCreated(budget, budgetRefusal);
        }

        var hours = (
            Development: JobStatistics.BudgetedHours(budgetTasks, TaskTypes.Development),
            Machining: JobStatistics.BudgetedHours(budgetTasks, TaskTypes.Machining),
            Assembly: JobStatistics.BudgetedHours(budgetTasks, TaskTypes.Assembly));
        var refusal = input.Refusal
            ?? (double.IsNaN(hours.Development + hours.Machining + hours.Assembly) ? "budgetedHours: Cast to Number failed for value NaN (a task without quantity)" : null);
        if (refusal is not null)
        {
            return NotCreated(budget, "D26, nothing written: " + refusal);
        }

        var marked = await budgets.SetHasGenerateJobAsync(budget.Id, cancellationToken);
        if (marked is null)
        {
            return Error.Validation(JobMessages.BudgetMissing); // deleted meanwhile
        }

        var job = await jobs.InsertAsync(
            new NewJob(budget.Id, input.FinalPrice, input.DueDate, input.State, input.OrderIdentification, input.IsOfficialOrderIdentification,
                userName, Now(), hours.Development, hours.Machining, hours.Assembly),
            cancellationToken);
        LogCreated(job.Id, budget.BudgetNumber, budget.Revision, budget.Id, job.State, userName);

        var created = new JobCreated(job, new BudgetDetails(marked, client, author));
        if (await MoveFoldersAsync(budget, revisionZero, client!, cancellationToken) is { } failed)
        {
            return failed;
        }

        await PrintLabelsAsync(budget, client!, job, cancellationToken);
        return created;
    }

    /// <summary>
    /// What Mongoose's <c>budget.save()</c> validation refuses: the required paths <c>client</c> and <c>madeBy</c>
    /// (populated: <c>null</c> when the document is gone), <c>descriptive</c>, <c>budgetNumber</c>,
    /// <c>paymentMethod</c> (non-empty strings) and <c>date</c>. Legacy answers 500 and writes nothing.
    /// </summary>
    private static string? BudgetSaveRefusal(Budget budget, BusinessEntity? client, BudgetAuthor? author)
    {
        var missing = new List<string>();
        if (client is null)
        {
            missing.Add("client");
        }

        if (author is null)
        {
            missing.Add("madeBy");
        }

        if (string.IsNullOrEmpty(budget.Descriptive))
        {
            missing.Add("descriptive");
        }

        if (string.IsNullOrEmpty(budget.BudgetNumber))
        {
            missing.Add("budgetNumber");
        }

        if (budget.Date is null)
        {
            missing.Add("date");
        }

        if (string.IsNullOrEmpty(budget.PaymentMethod))
        {
            missing.Add("paymentMethod");
        }

        return missing.Count == 0 ? null : "Budget validation failed: " + string.Join(", ", missing.Select(p => $"Path `{p}` is required."));
    }

    /// <summary>
    /// The NAS step (§5): <c>null</c> when done (or only logged), else the legacy 500. D3/D19: the budget folder is the
    /// one of revision 0 (its date's year and its descriptive: where create made it and a rename moved it); the job
    /// folder keeps the legacy formula (the job's revision: its date's year and descriptive).
    /// </summary>
    private async Task<Error?> MoveFoldersAsync(Budget budget, Budget revisionZero, BusinessEntity client, CancellationToken cancellationToken)
    {
        var source = new BudgetFolder(YearOf(revisionZero.Date), Js(client.Name), Js(revisionZero.BudgetNumber), Js(revisionZero.Descriptive));
        var target = new JobFolder(YearOf(budget.Date), Js(client.Name), Js(budget.BudgetNumber), Js(budget.Descriptive));
        var result = await nas.MoveBudgetFolderToJobAsync(source, target, cancellationToken);
        if (result == NasResult.Done)
        {
            return null;
        }

        LogFoldersNotMoved(budget.Id, budget.BudgetNumber, result);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = "the NAS folders could not be moved (the job is saved)" };
    }

    /// <summary>Legacy order: every <c>producao</c> printer, then every <c>maquinacao</c> printer, one after the other.</summary>
    private async Task PrintLabelsAsync(Budget budget, BusinessEntity client, Job job, CancellationToken cancellationToken)
    {
        var zone = options.Value.FindTimeZone() ?? TimeZoneInfo.Utc;
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(job.StartDate ?? Now(), DateTimeKind.Utc), zone));
        foreach (var template in PrintTemplates.All)
        {
            foreach (var printer in await printers.ListByTemplateAsync(template, cancellationToken))
            {
                var payload = LabelTemplates.Payload(printer.TemplateName, budget.BudgetNumber, budget.Descriptive, client.Name, start);
                await labels.PrintAsync(printer, payload, cancellationToken);
            }
        }
    }

    private Error NotCreated(Budget budget, string reason)
    {
        LogNotCreated(budget.Id, budget.BudgetNumber, reason);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
    }

    /// <summary>The year in the company's zone (legacy: the server's local time, <c>getFullYear()</c>); now for a missing date.</summary>
    private int YearOf(DateTime? utc)
    {
        var zone = options.Value.FindTimeZone() ?? TimeZoneInfo.Utc;
        var instant = utc is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : time.GetUtcNow();
        return TimeZoneInfo.ConvertTime(instant, zone).Year;
    }

    /// <summary>JavaScript <c>Date.now()</c>: UTC, millisecond precision.</summary>
    private DateTime Now()
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    /// <summary>A JavaScript template literal's text of a missing value.</summary>
    private static string Js(string? value) => value ?? "undefined";

    [LoggerMessage(EventId = 150, Level = LogLevel.Information,
        Message = "Job {JobId} created from budget {BudgetNumber} R{Revision} ({BudgetId}), state {State}, by {UserName}")]
    private partial void LogCreated(string jobId, string? budgetNumber, double revision, string budgetId, string state, string? userName);

    [LoggerMessage(EventId = 151, Level = LogLevel.Information, Message = "No job for budget {BudgetId} ({BudgetNumber}): a revision of the number has one")]
    private partial void LogAlreadyCreated(string budgetId, string? budgetNumber);

    [LoggerMessage(EventId = 152, Level = LogLevel.Warning, Message = "No job for budget {BudgetId} ({BudgetNumber}): {Reason}")]
    private partial void LogNotCreated(string budgetId, string? budgetNumber, string reason);

    [LoggerMessage(EventId = 153, Level = LogLevel.Error, Message = "Job of budget {BudgetId} ({BudgetNumber}) saved, but its NAS folders were not moved: {Result}; no labels printed")]
    private partial void LogFoldersNotMoved(string budgetId, string? budgetNumber, NasResult result);
}

/// <summary>A new job with the budget it was made from (populated, as saved: <c>hasGenerateJob</c> set).</summary>
public sealed record JobCreated(Job Job, BudgetDetails Budget);
