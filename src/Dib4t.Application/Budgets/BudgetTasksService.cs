using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Budgets;

/// <summary>
/// The routes of legacy <c>routes/task.js</c> (every route T, as legacy; PLAN.md §6): a budget's tasks, the task
/// types, the default task, edit and delete, and (D8) the intended create. Same order of checks and texts as the
/// legacy routes (checked in <c>budget-details-golden.json</c>); a failure legacy leaves to Express (a cast error,
/// missing prices) is a 500 <see cref="LegacyMessages.SomethingFailed"/> (D16). Edit, delete and create mark the
/// budget incomplete (legacy <c>setIncomplete</c>); the default task does not (the page saves the new total next).
/// </summary>
public sealed partial class BudgetTasksService(
    IBudgetRepository budgets,
    IBudgetTaskRepository tasks,
    IPricesRepository prices,
    ILogger<BudgetTasksService> logger)
{
    /// <summary><c>GET /getTaskTypes</c>: the legacy list, in its order.</summary>
    public static IReadOnlyList<string> TaskTypeList => TaskTypes.All;

    /// <summary><c>GET /getBudgetTasks/:budgetId</c>: id cast (500) → budget (400) → its tasks in natural order.</summary>
    public async Task<Result<IReadOnlyList<BudgetTask>>> ListAsync(string budgetId, CancellationToken cancellationToken)
    {
        var budget = await FindBudgetAsync(budgetId, cancellationToken);
        return budget.IsSuccess ? Result<IReadOnlyList<BudgetTask>>.Success(await tasks.ListByBudgetAsync(budget.Value.Id, cancellationToken)) : budget.Error;
    }

    /// <summary>
    /// <c>POST /createDefaultTask/:budgetId</c>: id cast (500) → budget (400) → job (400 <see cref="BudgetMessages.HasJob"/>)
    /// → prices (500 when missing) → an automation task at the automation price, quantity 1.
    /// </summary>
    public async Task<Result<BudgetTask>> CreateDefaultAsync(string budgetId, CancellationToken cancellationToken)
    {
        var found = await FindBudgetAsync(budgetId, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var budget = found.Value;
        if (budget.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        var current = await prices.FindAsync(cancellationToken);
        if (current?.Automation is not { } automation)
        {
            return NotSaved("default task", budget.Id, "no prices document (or no automation price): legacy TypeError");
        }

        var task = await tasks.InsertDefaultAsync(budget.Id, automation, cancellationToken);
        LogCreated(task.Id, budget.Id, budget.BudgetNumber, task.TaskType);
        return task;
    }

    /// <summary>
    /// D8: <c>POST /</c> with the intended schema (legacy refuses every body): Joi (400) → budget (400) → job (400) →
    /// the task, then the budget marked incomplete.
    /// </summary>
    public async Task<Result<BudgetTask>> CreateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetDetailsValidators.ValidateNewTask(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var (input, budgetId) = validated.Value;
        var budget = await budgets.FindByIdAsync(budgetId, cancellationToken);
        if (budget is null)
        {
            return Error.Validation(BudgetMessages.Missing);
        }

        if (budget.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        var task = await tasks.InsertAsync(budget.Id, input, cancellationToken);
        await budgets.SetIncompleteAsync(budget.Id, cancellationToken);
        LogCreated(task.Id, budget.Id, budget.BudgetNumber, task.TaskType);
        return task;
    }

    /// <summary>
    /// <c>PUT /:taskId/:budgetId</c>: Joi body (400) → Joi ids (400) → task (400 <see cref="BudgetMessages.TaskMissing"/>)
    /// → budget (400) → job (400) → the task saved (only what changed), then the budget marked incomplete. Like legacy,
    /// the task need not belong to the budget.
    /// </summary>
    public async Task<Result<BudgetTask>> UpdateAsync(string taskId, string budgetId, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetDetailsValidators.ValidateTask(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var ids = BudgetDetailsValidators.ValidateTaskIds(taskId, budgetId);
        if (!ids.IsSuccess)
        {
            return ids.Error;
        }

        var task = await tasks.FindByIdAsync(ids.Value.TaskId, cancellationToken);
        if (task is null)
        {
            return Error.Validation(BudgetMessages.TaskMissing);
        }

        var budget = await budgets.FindByIdAsync(ids.Value.BudgetId, cancellationToken);
        if (budget is null)
        {
            return Error.Validation(BudgetMessages.Missing);
        }

        if (budget.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        var saved = await tasks.UpdateAsync(task.Id, validated.Value, cancellationToken);
        if (saved is null)
        {
            return Error.Validation(BudgetMessages.TaskMissing); // deleted meanwhile
        }

        await budgets.SetIncompleteAsync(budget.Id, cancellationToken);
        LogUpdated(saved.Id, budget.Id, budget.BudgetNumber, saved.TaskType);
        return saved;
    }

    /// <summary>
    /// <c>DELETE /:taskId/:budgetId</c>: Joi ids (400) → budget (400) → job (400) → the budget marked incomplete → the
    /// task deleted (none: 500 <see cref="BudgetMessages.TaskNotDeleted"/>, the budget stays marked, as legacy).
    /// </summary>
    public async Task<Result<BudgetTask>> DeleteAsync(string taskId, string budgetId, CancellationToken cancellationToken)
    {
        var ids = BudgetDetailsValidators.ValidateTaskIds(taskId, budgetId);
        if (!ids.IsSuccess)
        {
            return ids.Error;
        }

        var budget = await budgets.FindByIdAsync(ids.Value.BudgetId, cancellationToken);
        if (budget is null)
        {
            return Error.Validation(BudgetMessages.Missing);
        }

        if (budget.HasGenerateJob)
        {
            return Error.Validation(BudgetMessages.HasJob);
        }

        await budgets.SetIncompleteAsync(budget.Id, cancellationToken);
        var deleted = await tasks.DeleteAsync(ids.Value.TaskId, cancellationToken);
        if (deleted is null)
        {
            LogNotDeleted(ids.Value.TaskId, budget.Id);
            return Error.Failure(BudgetMessages.TaskNotDeleted);
        }

        LogDeleted(deleted.Id, budget.Id, budget.BudgetNumber);
        return deleted;
    }

    /// <summary>Legacy <c>BudgetRepository.findById(req.params.budgetId)</c>: cast error 500, unknown 400.</summary>
    private async Task<Result<Budget>> FindBudgetAsync(string budgetId, CancellationToken cancellationToken)
    {
        if (LegacyObjectId.Cast(budgetId) is not { } id)
        {
            LogCastFailed(budgetId);
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        var budget = await budgets.FindByIdAsync(id, cancellationToken);
        return budget is null ? Error.Validation(BudgetMessages.Missing) : budget;
    }

    private Error NotSaved(string action, string subject, string reason)
    {
        LogNotSaved(action, subject, reason);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
    }

    [LoggerMessage(EventId = 90, Level = LogLevel.Information, Message = "Task {TaskId} ({TaskType}) added to budget {BudgetId} ({BudgetNumber})")]
    private partial void LogCreated(string taskId, string budgetId, string? budgetNumber, string? taskType);

    [LoggerMessage(EventId = 91, Level = LogLevel.Information, Message = "Task {TaskId} ({TaskType}) of budget {BudgetId} ({BudgetNumber}) updated")]
    private partial void LogUpdated(string taskId, string budgetId, string? budgetNumber, string? taskType);

    [LoggerMessage(EventId = 92, Level = LogLevel.Information, Message = "Task {TaskId} of budget {BudgetId} ({BudgetNumber}) deleted")]
    private partial void LogDeleted(string taskId, string budgetId, string? budgetNumber);

    [LoggerMessage(EventId = 93, Level = LogLevel.Warning, Message = "Task {TaskId} not deleted (budget {BudgetId}): no such task (legacy 500)")]
    private partial void LogNotDeleted(string taskId, string budgetId);

    [LoggerMessage(EventId = 94, Level = LogLevel.Warning, Message = "Task {Action} not saved ({Subject}): {Reason}")]
    private partial void LogNotSaved(string action, string subject, string reason);

    [LoggerMessage(EventId = 95, Level = LogLevel.Warning, Message = "Not a budget id: {Id} (legacy cast error, 500)")]
    private partial void LogCastFailed(string id);
}
