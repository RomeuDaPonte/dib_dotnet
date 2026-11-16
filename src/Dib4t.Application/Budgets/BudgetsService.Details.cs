using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.SharedKernel.Calculations;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Budgets;

/// <summary>
/// The budget details routes of legacy <c>routes/budget.js</c> (PLAN.md §6, §7.1): <c>patchProperty</c> and
/// <c>import</c>, in the legacy order of checks with the legacy texts (checked in <c>budget-details-golden.json</c>).
/// D24 (PLAN.md §9, owner 2026-11-16): the import never deletes anything. It copies the open budget's tasks
/// and/or description INTO the highest revision of the typed number (legacy: the other way round, replacing the
/// open budget's tasks, so importing from its own number deleted them).
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
    /// <c>PUT /import/:id</c> (D24): Joi (400 "Erro: …", legacy texts) → the receiving budget, the highest revision of
    /// the typed number (400 <see cref="BudgetMessages.ImportSourceMissing"/>) → id cast of the open budget (500) → the
    /// open budget (400 <see cref="BudgetMessages.NotFound"/>) → not into itself (400
    /// <see cref="BudgetMessages.ImportIntoItself"/>) → the receiving budget has no job (400
    /// <see cref="BudgetMessages.HasJob"/>) → copies of the open budget's tasks ADDED to it (when ticked), its
    /// description replaced by the open budget's (when ticked and there is one), its total recomputed from all its
    /// tasks with its own margin (when tasks were added), and its complete/PDF flags reset. Nothing is deleted; the
    /// open budget does not change. Answers the receiving budget.
    /// </summary>
    public async Task<Result<BudgetDetails>> ImportAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetDetailsValidators.ValidateImport(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var input = validated.Value;
        var revisions = await budgets.ListRevisionsAsync(input.BudgetNumber, cancellationToken); // highest revision first
        var target = revisions.Count > 0 ? revisions[0] : null;
        if (target is null)
        {
            return Error.Validation(BudgetMessages.ImportSourceMissing);
        }

        var found = await FindAsync(id, BudgetMessages.NotFound, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var source = found.Value;
        if (source.Id == target.Id)
        {
            return Error.Validation(BudgetMessages.ImportIntoItself);
        }

        if (target.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        if (!input.ImportTasks && !input.ImportDescription)
        {
            return await DetailsAsync(target, cancellationToken); // nothing to copy: nothing changes
        }

        int? copied = null;
        double? total = null;
        if (input.ImportTasks)
        {
            var sourceTasks = await tasks.ListByBudgetAsync(source.Id, cancellationToken);
            await tasks.InsertCopiesAsync(target.Id, sourceTasks, cancellationToken);
            copied = sourceTasks.Count;
            var all = await tasks.ListByBudgetAsync(target.Id, cancellationToken);
            var tasksTotal = BudgetCalculations.TasksTotal(all.Select(t => (t.Quantity ?? double.NaN, t.UnitPrice ?? double.NaN)));
            total = BudgetCalculations.TotalFromTasks(tasksTotal, target.Margin);
            if (!double.IsFinite(total.Value))
            {
                LogTotalNotComputed(target.Id, target.BudgetNumber);
                total = null; // a task without a number: the total stays as it is
            }
        }

        var description = input.ImportDescription ? source.Description : null; // none: the receiving budget keeps its own
        var saved = await budgets.ImportAsync(target.Id, new BudgetImport(total, description), cancellationToken);
        if (saved is null)
        {
            return Error.Validation(BudgetMessages.ImportSourceMissing); // deleted meanwhile
        }

        LogImported(saved.Id, saved.BudgetNumber, saved.Revision, source.Id, source.BudgetNumber, copied, description is not null);
        return await DetailsAsync(saved, cancellationToken);
    }

    [LoggerMessage(EventId = 80, Level = LogLevel.Information, Message = "Budget {BudgetId} ({BudgetNumber} R{Revision}): {Field} changed")]
    private partial void LogPatched(string budgetId, string? budgetNumber, double revision, string field);

    [LoggerMessage(EventId = 81, Level = LogLevel.Information,
        Message = "Budget {BudgetId} ({BudgetNumber} R{Revision}) imported from {SourceId} ({SourceNumber}): tasks added {TaskCount}, description replaced {Description}")]
    private partial void LogImported(string budgetId, string? budgetNumber, double revision, string sourceId, string? sourceNumber, int? taskCount, bool description);

    [LoggerMessage(EventId = 82, Level = LogLevel.Warning, Message = "Budget {BudgetId} ({BudgetNumber}): total not recomputed after an import (a task without a number)")]
    private partial void LogTotalNotComputed(string budgetId, string? budgetNumber);
}
