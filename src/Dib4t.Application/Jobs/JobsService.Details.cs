using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Jobs;

/// <summary>
/// The job details page's routes of legacy <c>routes/job.js</c> (PLAN.md §6, §7.3; checked against
/// <c>tests/fixtures/legacy/jobs/job-details-golden.json</c>): the state end dates, the registered costs, one header or
/// observations field, the verifications. Same order of checks and texts as legacy; a failure legacy leaves to Express
/// is the D16 500 with nothing written.
/// </summary>
public sealed partial class JobsService
{
    /// <summary>The legacy text of <c>PATCH singleProperty</c> for a delivery date outside "Montagem"/"Para faturação" (400).</summary>
    public const string DeliveryDateNotAllowed = "Estado de obra não permite alterar data de entrega";

    /// <summary>
    /// <c>PATCH statisticsDates/:jobId</c>: id (400) → <c>forJobState</c> (Joi, 400; "Para faturação"'s date is not
    /// one) → job (400 <see cref="JobMessages.NotFound"/>) → the date as Mongoose casts it. <b>D17 fixed</b>: a job without
    /// <c>jobStatisticsDates</c> gets one (legacy: 500).
    /// </summary>
    public async Task<Result<PopulatedJob>> ChangeStatisticsDateAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var id = JobValidators.ValidateId("jobId", jobId);
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var field = JobValidators.ValidateStateDateName(body);
        if (!field.IsSuccess)
        {
            return field.Error;
        }

        var read = await jobs.FindByIdAsync(id.Value, JobPopulation.BudgetClientAndAuthor, cancellationToken);
        if (read is null)
        {
            return Error.Validation(JobMessages.NotFound);
        }

        var changes = new JobChanges(read.Job, Now());
        changes.SetStatisticsDate(field.Value, Get(JsObject.Properties(body), "newDate"));
        var saved = await SaveAsync(read, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            var date = saved.Value.Job.StatisticsDates?.Get(field.Value) ?? Stored<DateTime>.Missing;
            LogStatisticsDateChanged(read.Job.Id, field.Value, date.HasValue ? date.Value : null);
        }

        return saved;
    }

    /// <summary>
    /// <c>PATCH register/:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>) →
    /// <see cref="JobChanges.SetRegisteredValue"/> (no validation: legacy stores what Mongoose casts; D18 fixed).
    /// </summary>
    public async Task<Result<PopulatedJob>> RegisterValueAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(jobId, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var values = JsObject.Properties(body);
        var taskType = Get(values, "taskType");
        var changes = new JobChanges(read.Value.Job, Now());
        try
        {
            changes.SetRegisteredValue(Get(values, "newRegisteredValue"), taskType);
        }
        catch (JobScriptException error)
        {
            return ScriptFailed(read.Value.Job, error.Message);
        }

        var saved = await SaveAsync(read.Value, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogValueRegistered(read.Value.Job.Id, taskType is { ValueKind: JsonValueKind.String } type ? type.GetString() : null, changes.Assignments.Count > 0);
        }

        return saved;
    }

    /// <summary>
    /// <c>PATCH singleProperty/:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>) → a delivery date only
    /// in "Montagem" or "Para faturação" (400 <see cref="DeliveryDateNotAllowed"/>, checked before Joi as legacy) →
    /// Joi (400) → the field as Mongoose casts it.
    /// </summary>
    public async Task<Result<PopulatedJob>> ChangeSinglePropertyAsync(string jobId, JsonElement body, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(jobId, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var values = JsObject.Properties(body);
        if (Get(values, "propertyName") is { ValueKind: JsonValueKind.String } name && name.GetString() == "deliveryDate"
            && read.Value.Job.State is not (JobStates.Assembly or JobStates.ForInvoicing))
        {
            return Error.Validation(DeliveryDateNotAllowed);
        }

        var property = JobValidators.ValidateSingleProperty(body);
        if (!property.IsSuccess)
        {
            return property.Error;
        }

        var changes = new JobChanges(read.Value.Job, Now());
        changes.SetSingleProperty(property.Value, Get(values, "value"));
        var saved = await SaveAsync(read.Value, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogSinglePropertyChanged(read.Value.Job.Id, property.Value);
        }

        return saved;
    }

    /// <summary>
    /// <c>PATCH updateVerifications/:jobId</c>: id (400) → job (400 <see cref="JobMessages.NotFound"/>) → with
    /// <c>updateMachiningVerificatons == true</c> the machining statistic is replaced by the sent one, else the
    /// assembly statistic; <c>validatedBy</c> = the signed-in user's name.
    /// </summary>
    public async Task<Result<PopulatedJob>> UpdateVerificationsAsync(string jobId, JsonElement body, string? userName, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(jobId, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var values = JsObject.Properties(body);
        var machining = JobChanges.IsMachiningVerification(Get(values, "updateMachiningVerificatons"));
        var changes = new JobChanges(read.Value.Job, Now());
        if (machining)
        {
            changes.ReplaceMachiningStatistic(Get(values, "machiningStatistic"), userName);
        }
        else
        {
            changes.ReplaceAssemblyStatistic(Get(values, "assemblyStatistic"), userName);
        }

        var saved = await SaveAsync(read.Value, changes, cancellationToken);
        if (saved.IsSuccess)
        {
            LogVerificationsUpdated(read.Value.Job.Id, machining ? "machining" : "assembly", userName);
        }

        return saved;
    }

    [LoggerMessage(EventId = 159, Level = LogLevel.Information, Message = "Job {JobId} {Field} now {Date:o}")]
    private partial void LogStatisticsDateChanged(string jobId, string field, DateTime? date);

    [LoggerMessage(EventId = 167, Level = LogLevel.Information, Message = "Job {JobId} registered value of {TaskType} saved (a known type: {Known})")]
    private partial void LogValueRegistered(string jobId, string? taskType, bool known);

    [LoggerMessage(EventId = 168, Level = LogLevel.Information, Message = "Job {JobId} {PropertyName} changed")]
    private partial void LogSinglePropertyChanged(string jobId, string propertyName);

    [LoggerMessage(EventId = 169, Level = LogLevel.Information, Message = "Job {JobId} {Statistic} verifications saved by {UserName}")]
    private partial void LogVerificationsUpdated(string jobId, string statistic, string? userName);
}
