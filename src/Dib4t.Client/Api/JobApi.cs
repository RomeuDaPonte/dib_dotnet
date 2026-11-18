using Dib4t.Contracts.Jobs;

namespace Dib4t.Client.Api;

/// <summary>The job routes of <c>/api/job</c> (PLAN.md §6, §8.2). This step: create.</summary>
public interface IJobApi
{
    /// <summary><c>POST /api/job</c>: the new job, with the budget it was made from.</summary>
    Task<ApiResult<JobResponse>> CreateAsync(NewJobRequest job);
}

/// <inheritdoc cref="IJobApi"/>
public sealed class JobApi(HttpClient http) : IJobApi
{
    public Task<ApiResult<JobResponse>> CreateAsync(NewJobRequest job) => ApiCalls.SendAsync<JobResponse>(http, HttpMethod.Post, "api/job", job);
}
