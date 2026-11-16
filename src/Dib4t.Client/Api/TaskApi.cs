using Dib4t.Contracts.Budgets;

namespace Dib4t.Client.Api;

/// <summary>The task routes of <c>/api/task</c> used by the budget details page (PLAN.md §6, §8.2).</summary>
public interface ITaskApi
{
    /// <summary><c>GET /api/task/getBudgetTasks/:budgetId</c>: the budget's tasks in stored order.</summary>
    Task<ApiResult<IReadOnlyList<BudgetTaskResponse>>> ListAsync(string budgetId);

    /// <summary><c>GET /api/task/getTaskTypes</c>.</summary>
    Task<ApiResult<IReadOnlyList<string>>> ListTypesAsync();

    /// <summary><c>POST /api/task/createDefaultTask/:budgetId</c>: an automation task at the automation price.</summary>
    Task<ApiResult<BudgetTaskResponse>> CreateDefaultAsync(string budgetId);

    /// <summary><c>PUT /api/task/:taskId/:budgetId</c>.</summary>
    Task<ApiResult<BudgetTaskResponse>> UpdateAsync(string taskId, string budgetId, UpdateTaskRequest task);

    /// <summary><c>DELETE /api/task/:taskId/:budgetId</c>: the deleted task.</summary>
    Task<ApiResult<BudgetTaskResponse>> DeleteAsync(string taskId, string budgetId);
}

/// <inheritdoc cref="ITaskApi"/>
public sealed class TaskApi(HttpClient http) : ITaskApi
{
    private const string Root = "api/task";

    public Task<ApiResult<IReadOnlyList<BudgetTaskResponse>>> ListAsync(string budgetId) =>
        ApiCalls.SendAsync<IReadOnlyList<BudgetTaskResponse>>(http, HttpMethod.Get, Root + "/getBudgetTasks/" + Uri.EscapeDataString(budgetId));

    public Task<ApiResult<IReadOnlyList<string>>> ListTypesAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<string>>(http, HttpMethod.Get, Root + "/gettasktypes");

    public Task<ApiResult<BudgetTaskResponse>> CreateDefaultAsync(string budgetId) =>
        ApiCalls.SendAsync<BudgetTaskResponse>(http, HttpMethod.Post, Root + "/createDefaultTask/" + Uri.EscapeDataString(budgetId), new { });

    public Task<ApiResult<BudgetTaskResponse>> UpdateAsync(string taskId, string budgetId, UpdateTaskRequest task) =>
        ApiCalls.SendAsync<BudgetTaskResponse>(http, HttpMethod.Put, $"{Root}/{Uri.EscapeDataString(taskId)}/{Uri.EscapeDataString(budgetId)}", task);

    public Task<ApiResult<BudgetTaskResponse>> DeleteAsync(string taskId, string budgetId) =>
        ApiCalls.SendAsync<BudgetTaskResponse>(http, HttpMethod.Delete, $"{Root}/{Uri.EscapeDataString(taskId)}/{Uri.EscapeDataString(budgetId)}");
}
