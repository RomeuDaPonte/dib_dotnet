using System.Net;
using System.Text.Json;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Jobs;

namespace Dib4t.Client.Api;

/// <summary>The job routes of <c>/api/job</c> (PLAN.md §6, §8.2): create and the jobs board's routes.</summary>
public interface IJobApi
{
    /// <summary><c>POST /api/job</c>: the new job, with the budget it was made from.</summary>
    Task<ApiResult<JobResponse<BudgetResponse>>> CreateAsync(NewJobRequest job);

    /// <summary><c>GET getUnfinished</c>: the open jobs, by due date and priority.</summary>
    Task<ApiResult<IReadOnlyList<JobResponse<JobListBudgetResponse>>>> ListUnfinishedAsync();

    /// <summary><c>GET getByState/:state</c>.</summary>
    Task<ApiResult<IReadOnlyList<JobResponse<JobListBudgetResponse>>>> ListByStateAsync(string state);

    /// <summary><c>GET getCounts</c>: the tab counters.</summary>
    Task<ApiResult<JobCountsResponse>> CountsAsync();

    /// <summary><c>GET getById/:jobId</c>: the job (a missing one is an unexpected error).</summary>
    Task<ApiResult<JobResponse<BudgetResponse>>> GetByIdAsync(string jobId);

    /// <summary><c>GET getByBudgetNumber/:number</c>: the job of a budget number, or a success without value when there is none.</summary>
    Task<ApiResult<JobResponse<BudgetRefsResponse>?>> FindByBudgetNumberAsync(string budgetNumber);

    /// <summary>
    /// <c>PUT statistics/:jobId</c>: the saved job. "Faturada" below 100 % billed is the legacy 405: a failure whose
    /// <see cref="StatisticsSave.Refused"/> holds the reason and the saved job.
    /// </summary>
    Task<StatisticsSave> SaveStatisticsAsync(string jobId, IReadOnlyDictionary<string, object?> body);

    /// <summary><c>PATCH dueDate/:jobId</c>.</summary>
    Task<ApiResult<JobResponse<BudgetResponse>>> ChangeDueDateAsync(string jobId, string dueDate);

    /// <summary><c>PATCH state/:jobId</c>.</summary>
    Task<ApiResult<JobResponse<BudgetResponse>>> ChangeStateAsync(string jobId, string state);

    /// <summary><c>PATCH changeJobPriority/:jobId</c>.</summary>
    Task<ApiResult<JobResponse<BudgetResponse>>> ChangePriorityAsync(string jobId, string priority);
}

/// <summary>A statistics save: the answer, and the 405's reason and saved job.</summary>
public sealed record StatisticsSave(ApiResult<JobResponse<BudgetResponse>> Result, JobStatisticsRefusedResponse? Refused);

/// <inheritdoc cref="IJobApi"/>
public sealed class JobApi(HttpClient http) : IJobApi
{
    public Task<ApiResult<JobResponse<BudgetResponse>>> CreateAsync(NewJobRequest job) =>
        ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Post, "api/job", job);

    public Task<ApiResult<IReadOnlyList<JobResponse<JobListBudgetResponse>>>> ListUnfinishedAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<JobResponse<JobListBudgetResponse>>>(http, HttpMethod.Get, "api/job/getUnfinished");

    public Task<ApiResult<IReadOnlyList<JobResponse<JobListBudgetResponse>>>> ListByStateAsync(string state) =>
        ApiCalls.SendAsync<IReadOnlyList<JobResponse<JobListBudgetResponse>>>(http, HttpMethod.Get, "api/job/getbystate/" + Uri.EscapeDataString(state));

    public Task<ApiResult<JobCountsResponse>> CountsAsync() => ApiCalls.SendAsync<JobCountsResponse>(http, HttpMethod.Get, "api/job/getCounts");

    public Task<ApiResult<JobResponse<BudgetResponse>>> GetByIdAsync(string jobId) =>
        ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Get, "api/job/getById/" + Uri.EscapeDataString(jobId));

    public async Task<ApiResult<JobResponse<BudgetRefsResponse>?>> FindByBudgetNumberAsync(string budgetNumber)
    {
        var raw = await ApiCalls.SendAsync<string>(http, HttpMethod.Get, "api/job/getByBudgetNumber/" + Uri.EscapeDataString(budgetNumber), null, raw: true);
        if (!raw.IsSuccess)
        {
            return ApiResult.Failure<JobResponse<BudgetRefsResponse>?>(raw.Status, raw.ErrorText!);
        }

        return raw.Value is { Length: > 0 } text
            ? ApiResult.Success<JobResponse<BudgetRefsResponse>?>(JsonSerializer.Deserialize<JobResponse<BudgetRefsResponse>>(text, ApiCalls.Json))
            : new ApiResult<JobResponse<BudgetRefsResponse>?>(null, raw.Status, null); // legacy: an empty 200
    }

    public async Task<StatisticsSave> SaveStatisticsAsync(string jobId, IReadOnlyDictionary<string, object?> body)
    {
        var result = await ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Put, "api/job/statistics/" + Uri.EscapeDataString(jobId), body);
        if (result.Status == HttpStatusCode.MethodNotAllowed && result.ErrorText is { } text)
        {
            try
            {
                return new StatisticsSave(result, JsonSerializer.Deserialize<JobStatisticsRefusedResponse>(text, ApiCalls.Json));
            }
            catch (JsonException)
            {
                // not the legacy 405 body: shown as the error text
            }
        }

        return new StatisticsSave(result, null);
    }

    public Task<ApiResult<JobResponse<BudgetResponse>>> ChangeDueDateAsync(string jobId, string dueDate) =>
        ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Patch, "api/job/dueDate/" + Uri.EscapeDataString(jobId), new JobDueDateRequest(dueDate));

    public Task<ApiResult<JobResponse<BudgetResponse>>> ChangeStateAsync(string jobId, string state) =>
        ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Patch, "api/job/state/" + Uri.EscapeDataString(jobId), new JobStateRequest(state));

    public Task<ApiResult<JobResponse<BudgetResponse>>> ChangePriorityAsync(string jobId, string priority) =>
        ApiCalls.SendAsync<JobResponse<BudgetResponse>>(http, HttpMethod.Patch, "api/job/changeJobPriority/" + Uri.EscapeDataString(jobId),
            new JobPriorityRequest(priority));
}
