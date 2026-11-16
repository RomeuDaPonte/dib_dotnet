using Dib4t.Contracts.Logs;

namespace Dib4t.Client.Api;

/// <summary><c>/api/logs</c> (Registos tab, <c>canSeeDefinicoes</c>; PLAN.md §3.3).</summary>
public interface ILogsApi
{
    /// <summary>A page for <paramref name="query"/>, the query string without <c>?</c> (built by <c>LogsView</c>).</summary>
    Task<ApiResult<LogPageResponse>> SearchAsync(string query);

    Task<ApiResult<LogFacetsResponse>> FacetsAsync();
}

/// <inheritdoc cref="ILogsApi"/>
public sealed class LogsApi(HttpClient http) : ILogsApi
{
    public Task<ApiResult<LogPageResponse>> SearchAsync(string query) =>
        ApiCalls.SendAsync<LogPageResponse>(http, HttpMethod.Get, string.IsNullOrEmpty(query) ? "api/logs" : "api/logs?" + query);

    public Task<ApiResult<LogFacetsResponse>> FacetsAsync() =>
        ApiCalls.SendAsync<LogFacetsResponse>(http, HttpMethod.Get, "api/logs/facets");
}
