using System.Text.Json;
using Dib4t.Application.Common;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Budgets;

/// <summary>
/// The budget details routes of legacy <c>routes/budget.js</c> (PLAN.md §6, §7.1): <c>patchProperty</c> and
/// <c>import</c>, in the legacy order of checks with the legacy texts (checked in <c>budget-details-golden.json</c>).
/// D24 (PLAN.md §9, owner 2026-11-16: keep the legacy behavior): the budget's tasks are deleted before the source's
/// are read, so importing from the budget's own number leaves it without tasks, as in legacy.
/// </summary>
public sealed partial class BudgetsService
{
    /// <summary>
    /// <c>PUT /patchProperty/:id</c>: Joi (400 "Não é possível atualizar …") → id cast (500) → budget (400
    /// <see cref="BudgetMessages.PatchMissing"/>) → a budget with a job is answered <b>unchanged with 200</b> → save
    /// (flags reset, then the property; a value Mongoose cannot cast is a 500).
    /// </summary>
    public async Task<Result<BudgetDetails>> PatchAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetDetailsValidators.ValidatePatch(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var found = await FindAsync(id, BudgetMessages.PatchMissing, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var budget = found.Value;
        if (budget.HasGenerateJob)
        {
            LogLocked(budget.Id, budget.BudgetNumber);
            return await DetailsAsync(budget, cancellationToken);
        }

        var patch = validated.Value;
        if (patch.CastFailure is { } reason)
        {
            return NotSaved("patch", budget.Id, reason); // the save's validation, after the lookups (legacy order)
        }

        var saved = await budgets.PatchAsync(budget.Id, patch, cancellationToken);
        if (saved is null)
        {
            return Error.Validation(BudgetMessages.PatchMissing); // deleted meanwhile
        }

        LogPatched(saved.Id, saved.BudgetNumber, saved.Revision, patch.Field);
        return await DetailsAsync(saved, cancellationToken);
    }

    /// <summary>
    /// <c>PUT /import/:id</c>: Joi (400 "Erro: …") → the source, the first document with the number (400
    /// <see cref="BudgetMessages.ImportSourceMissing"/>) → id cast (500) → budget (400 <see cref="BudgetMessages.NotFound"/>)
    /// → job (400 <see cref="BudgetMessages.HasJob"/>) → tasks (replaced by copies of the source's; total and margin
    /// copied) and/or description → save. The complete/PDF flags stay as they are (legacy).
    /// </summary>
    public async Task<Result<BudgetDetails>> ImportAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetDetailsValidators.ValidateImport(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var input = validated.Value;
        var source = await budgets.FindFirstByNumberAsync(input.BudgetNumber, cancellationToken);
        if (source is null)
        {
            return Error.Validation(BudgetMessages.ImportSourceMissing);
        }

        var found = await FindAsync(id, BudgetMessages.NotFound, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var budget = found.Value;
        if (budget.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        var copied = 0;
        if (input.ImportTasks)
        {
            // Legacy order (D24 kept): delete, then read the source (its own number: nothing left to copy).
            await tasks.DeleteByBudgetAsync(budget.Id, cancellationToken);
            var sourceTasks = await tasks.ListByBudgetAsync(source.Id, cancellationToken);
            await tasks.InsertCopiesAsync(budget.Id, sourceTasks, cancellationToken);
            copied = sourceTasks.Count;
        }

        var saved = await budgets.ImportAsync(
            budget.Id,
            new BudgetImport(input.ImportTasks, source.Total, source.Margin, input.ImportDescription, source.Description),
            cancellationToken);
        if (saved is null)
        {
            return Error.Validation(BudgetMessages.NotFound); // deleted meanwhile
        }

        LogImported(saved.Id, saved.BudgetNumber, source.Id, source.BudgetNumber, input.ImportTasks ? copied : null, input.ImportDescription);
        return await DetailsAsync(saved, cancellationToken);
    }

    [LoggerMessage(EventId = 80, Level = LogLevel.Information, Message = "Budget {BudgetId} ({BudgetNumber} R{Revision}): {Field} changed")]
    private partial void LogPatched(string budgetId, string? budgetNumber, double revision, string field);

    [LoggerMessage(EventId = 81, Level = LogLevel.Information,
        Message = "Budget {BudgetId} ({BudgetNumber}) imported from {SourceId} ({SourceNumber}): tasks copied {TaskCount}, description {Description}")]
    private partial void LogImported(string budgetId, string? budgetNumber, string sourceId, string? sourceNumber, int? taskCount, bool description);
}
