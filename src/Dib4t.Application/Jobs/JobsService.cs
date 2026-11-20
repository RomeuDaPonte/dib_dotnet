using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
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

        await PrintCreateLabelsAsync(budget, client!, job, cancellationToken);
        return created;
    }

    /// <summary>
    /// <c>PUT statistics/:jobId</c> (the statistics dialog, PLAN.md §7.3): the id (400) → the job (400
    /// <see cref="JobMessages.NotFoundForStatistics"/>) → the schema of its <b>current</b> state (400) → a job without
    /// statistics gets new ones and the requested state → otherwise: the deviation of the current state's task type
    /// (400 <see cref="JobMessages.WrongDeviation"/>) → <c>setStatistics</c> (<c>validatedBy</c>/<c>invoicedBy</c> = the
    /// signed-in user's name) → the missing end dates → the state; "Faturada" below 100 % billed is saved without the
    /// state and answered 405 (<see cref="JobStatisticsSaved.StateChanged"/> false). What the legacy code fails on
    /// (a missing budget, comment types that are not a list, a value Mongoose refuses) is the 500 with nothing written.
    /// </summary>
    public async Task<Result<JobStatisticsSaved>> SaveStatisticsAsync(string jobId, JsonElement body, string? userName, CancellationToken cancellationToken)
    {
        var id = JobValidators.ValidateId("jobId", jobId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var read = await jobs.FindByIdAsync(id.Value, JobPopulation.BudgetClientAndAuthor, cancellationToken);
        if (read is null)
        {
            return Error.Validation(JobMessages.NotFoundForStatistics);
        }

        var job = read.Job;
        if (JobValidators.ValidateStatistics(body, job.State) is { } message)
        {
            return Error.Validation(message);
        }

        var values = JsObject.Properties(body);
        var newState = values.TryGetValue("state", out var state) ? state : (JsonElement?)null;
        var changes = new JobChanges(job, Now());
        if (read.Budget is null)
        {
            return ScriptFailed(job, "TypeError: Cannot read properties of null (reading '_id')");
        }

        bool stateChanged;
        if (job.DevStatistic is null)
        {
            changes.SetNewStatistics(await tasks.ListByBudgetAsync(read.Budget.Budget.Id, cancellationToken));
            changes.ChangeState(newState); // legacy ignores the outcome here
            stateChanged = true;
        }
        else
        {
            var type = JobChanges.TaskTypeOf(job.State);
            var related = (await tasks.ListByBudgetAsync(read.Budget.Budget.Id, cancellationToken))
                .Where(t => string.Equals(t.TaskType, type, StringComparison.Ordinal)).ToList();
            if (!JobChanges.ConfirmDeviation(related, Get(values, "numberOfHoursUsed"), Get(values, "deviation")))
            {
                return Error.Validation(JobMessages.WrongDeviation);
            }

            try
            {
                changes.SetStatistics(body, userName);
            }
            catch (JobScriptException error)
            {
                return ScriptFailed(job, error.Message);
            }

            changes.UpdateStateDates(newState);
            stateChanged = changes.ChangeState(newState);
        }

        var saved = await SaveAsync(read, changes, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        LogStatisticsSaved(job.Id, job.State, saved.Value.Job.State, stateChanged, userName);
        return new JobStatisticsSaved(saved.Value, stateChanged);
    }

    /// <summary><c>PATCH dueDate/:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>) → the date as Mongoose casts it (no check before; a missing, empty or invalid date is the legacy 500).</summary>
    public async Task<Result<PopulatedJob>> ChangeDueDateAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(jobId, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var changes = new JobChanges(read.Value.Job, Now());
        changes.ChangeDueDate(Get(JsObject.Properties(body), "dueDate"));
        var saved = await SaveAsync(read.Value, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogDueDateChanged(read.Value.Job.Id, saved.Value.Job.DueDate);
        }

        return saved;
    }

    /// <summary>
    /// <c>PATCH state/:jobId</c>: id (400) → the state (Joi, 400) → job (400 <see cref="JobMessages.NotFound"/>) →
    /// <c>changeState</c> (to "Faturada" only at 100 % billed; otherwise the job is answered unchanged). <b>D1 fixed</b>:
    /// legacy looks the job up by <c>req.params.id</c> (undefined), so it always answers 400 "Obra não existe".
    /// </summary>
    public async Task<Result<PopulatedJob>> ChangeStateAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var id = JobValidators.ValidateId("jobId", jobId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var state = JobValidators.ValidateState(body);
        if (!state.IsSuccess)
        {
            return state.Error;
        }

        var read = await jobs.FindByIdAsync(id.Value, JobPopulation.BudgetClientAndAuthor, cancellationToken);
        if (read is null)
        {
            return Error.Validation(JobMessages.NotFound);
        }

        var changes = new JobChanges(read.Job, Now());
        changes.ChangeState(JsonSerializer.SerializeToElement(state.Value));
        var saved = await SaveAsync(read, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogStateChanged(read.Job.Id, read.Job.State, saved.Value.Job.State);
        }

        return saved;
    }

    /// <summary><c>PATCH changeJobPriority/:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>) → the priority (Joi, 400) → saved.</summary>
    public async Task<Result<PopulatedJob>> ChangePriorityAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(jobId, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var priority = JobValidators.ValidatePriority(body);
        if (!priority.IsSuccess)
        {
            return priority.Error;
        }

        var changes = new JobChanges(read.Value.Job, Now());
        changes.SetPriority(priority.Value);
        var saved = await SaveAsync(read.Value, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogPriorityChanged(read.Value.Job.Id, read.Value.Job.Priority, priority.Value);
        }

        return saved;
    }

    /// <summary><c>GET /:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>), budget, client and author populated.</summary>
    public Task<Result<PopulatedJob>> GetAsync(string jobId, CancellationToken cancellationToken) => ReadAsync(jobId, cancellationToken);

    /// <summary>
    /// <c>GET getById/:jobId</c> (no Joi): the job or <c>null</c> (legacy: an empty 200); an id Mongoose cannot cast
    /// (<see cref="LegacyObjectId"/>) is the legacy 500.
    /// </summary>
    public async Task<Result<PopulatedJob?>> FindAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (LegacyObjectId.Cast(jobId) is not { } id)
        {
            return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = $"CastError: Cast to ObjectId failed for value \"{jobId}\"" };
        }

        return await jobs.FindByIdAsync(id, JobPopulation.BudgetClientAndAuthor, cancellationToken);
    }

    /// <summary><c>GET getCounts</c>: the open jobs (not billed or deleted) and the jobs of each state before billing.</summary>
    public async Task<JobCounts> CountAsync(CancellationToken cancellationToken)
    {
        var open = await jobs.CountAsync(JobFilter.NotIn(JobStates.Billed, JobStates.Deleted), cancellationToken);
        var counts = new int[6];
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = await jobs.CountAsync(JobFilter.InState(JobValidators.InitialStates[i]), cancellationToken);
        }

        return new JobCounts(open, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
    }

    /// <summary><c>GET getUnfinished</c>: the jobs not billed or deleted, by due date, then by priority (<see cref="JobBoard.ByPriority"/>).</summary>
    public async Task<IReadOnlyList<PopulatedJob>> ListUnfinishedAsync(CancellationToken cancellationToken) =>
        JobBoard.ByPriority(await jobs.ListAsync(JobFilter.NotIn(JobStates.Billed, JobStates.Deleted), int.MaxValue, cancellationToken));

    /// <summary><c>GET getAll</c>: every job (at most 10,000), by due date, then by priority.</summary>
    public async Task<IReadOnlyList<PopulatedJob>> ListAllAsync(CancellationToken cancellationToken) =>
        JobBoard.ByPriority(await jobs.ListAsync(JobFilter.All, 10000, cancellationToken));

    /// <summary>
    /// <c>GET getByState/:state</c>: the state (Joi, 400) → its jobs by due date; then by priority, except "Faturada"
    /// and "Para faturação" (legacy keeps them by date).
    /// </summary>
    public async Task<Result<IReadOnlyList<PopulatedJob>>> ListByStateAsync(string state, CancellationToken cancellationToken)
    {
        var validated = JobValidators.ValidateState(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["state"] = state }));
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var list = await jobs.ListAsync(JobFilter.InState(validated.Value), int.MaxValue, cancellationToken);
        return validated.Value is JobStates.Billed or JobStates.ForInvoicing ? Result<IReadOnlyList<PopulatedJob>>.Success(list) : Result<IReadOnlyList<PopulatedJob>>.Success(JobBoard.ByPriority(list));
    }

    /// <summary><c>GET getByBudgetNumber/:budgetNumber</c>: the first job of a budget with that number, or <c>null</c> (an empty 200).</summary>
    public Task<PopulatedJob?> FindByBudgetNumberAsync(string budgetNumber, CancellationToken cancellationToken) =>
        jobs.FindByBudgetNumberAsync(budgetNumber, cancellationToken);

    /// <summary><c>GET getByBudgetId/:budgetId</c>: the id (400) → the job's id (400 <see cref="JobMessages.NoJobForBudget"/>).</summary>
    public async Task<Result<string>> FindIdByBudgetIdAsync(string budgetId, CancellationToken cancellationToken)
    {
        var id = JobValidators.ValidateId("budgetId", budgetId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        return await jobs.FindIdByBudgetIdAsync(id.Value, cancellationToken) is { } jobId ? jobId : Error.Validation(JobMessages.NoJobForBudget);
    }

    private async Task<Result<PopulatedJob>> ReadAsync(string jobId, CancellationToken cancellationToken)
    {
        var id = JobValidators.ValidateId("jobId", jobId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        return await jobs.FindByIdAsync(id.Value, JobPopulation.BudgetClientAndAuthor, cancellationToken) is { } read
            ? read
            : Error.Validation(JobMessages.NotFound);
    }

    /// <summary>The legacy <c>save()</c>: refused by Mongoose's validation → 500, nothing written.</summary>
    private async Task<Result<PopulatedJob>> SaveAsync(PopulatedJob read, JobChanges changes, CancellationToken cancellationToken)
    {
        if (changes.Refusal is { } refusal)
        {
            LogNotSaved(read.Job.Id, refusal);
            return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = "Job validation failed: " + refusal };
        }

        return await jobs.SaveAsync(read, changes.Assignments, cancellationToken);
    }

    private Error ScriptFailed(Job job, string reason)
    {
        LogNotSaved(job.Id, reason);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
    }

    private static JsonElement? Get(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

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
    private async Task PrintCreateLabelsAsync(Budget budget, BusinessEntity client, Job job, CancellationToken cancellationToken)
    {
        var start = LabelDate(job.StartDate ?? Now());
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

    [LoggerMessage(EventId = 154, Level = LogLevel.Information,
        Message = "Job {JobId} statistics of {CurrentState} saved by {UserName}; state now {State} (requested state applied: {StateChanged})")]
    private partial void LogStatisticsSaved(string jobId, string currentState, string state, bool stateChanged, string? userName);

    [LoggerMessage(EventId = 155, Level = LogLevel.Information, Message = "Job {JobId} due date now {DueDate:o}")]
    private partial void LogDueDateChanged(string jobId, DateTime? dueDate);

    [LoggerMessage(EventId = 156, Level = LogLevel.Information, Message = "Job {JobId} state {From} → {To}")]
    private partial void LogStateChanged(string jobId, string from, string to);

    [LoggerMessage(EventId = 157, Level = LogLevel.Information, Message = "Job {JobId} priority {From} → {To}")]
    private partial void LogPriorityChanged(string jobId, string? from, string to);

    [LoggerMessage(EventId = 158, Level = LogLevel.Warning, Message = "Job {JobId} not saved (legacy 500, nothing written): {Reason}")]
    private partial void LogNotSaved(string jobId, string reason);

    [LoggerMessage(EventId = 153, Level = LogLevel.Error, Message = "Job of budget {BudgetId} ({BudgetNumber}) saved, but its NAS folders were not moved: {Result}; no labels printed")]
    private partial void LogFoldersNotMoved(string budgetId, string? budgetNumber, NasResult result);
}

/// <summary>A statistics save: the job as saved; <see cref="StateChanged"/> false = "Faturada" refused below 100 % (legacy 405).</summary>
public sealed record JobStatisticsSaved(PopulatedJob Job, bool StateChanged);

/// <summary><c>GET getCounts</c> (the board's tab counters).</summary>
public sealed record JobCounts(int All, int Development, int OnApproval, int Approved, int Machining, int Assembly, int ForInvoicing);

/// <summary>The board's ordering (legacy <c>orderArray</c> of <c>repositories/jobRepository.js</c>).</summary>
public static class JobBoard
{
    /// <summary>"Alta" first, then "Média", then the rest ("Baixa", none), each keeping its order (the due date).</summary>
    public static IReadOnlyList<T> ByPriority<T>(IReadOnlyList<T> jobs, Func<T, string?> priority)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(priority);
        return [.. jobs.Where(j => priority(j) == JobPriorities.High), .. jobs.Where(j => priority(j) == JobPriorities.Medium),
            .. jobs.Where(j => priority(j) is not (JobPriorities.High or JobPriorities.Medium))];
    }

    /// <summary><see cref="ByPriority{T}(IReadOnlyList{T}, Func{T, string?})"/> of read jobs.</summary>
    public static IReadOnlyList<PopulatedJob> ByPriority(IReadOnlyList<PopulatedJob> jobs) => ByPriority(jobs, j => j.Job.Priority);
}

/// <summary>A new job with the budget it was made from (populated, as saved: <c>hasGenerateJob</c> set).</summary>
public sealed record JobCreated(Job Job, BudgetDetails Budget);
