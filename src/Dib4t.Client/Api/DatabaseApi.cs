using Dib4t.Contracts.Database;

namespace Dib4t.Client.Api;

/// <summary>
/// <c>GET /api/database/info</c> (Base de Dados tab, <c>canSeeDefinicoes</c>). The backup and the restore do not go
/// through <see cref="HttpClient"/> (a 1 GB file must not pass through WASM memory): see
/// <see cref="Settings.DatabaseTransfers"/>.
/// </summary>
public interface IDatabaseApi
{
    /// <summary>
    /// The info, or the status and the answer's text as it is for every failure (legacy <c>readErrorMessage</c> shows
    /// the server's text, 5xx included; the global notification has been shown for those already).
    /// </summary>
    Task<ApiResult<DatabaseInfoResponse>> InfoAsync();

    /// <summary>
    /// <c>GET /api/database/restore/status</c>: the running or the last restore since the server started, or a success
    /// with <see langword="null"/> when there was none (204).
    /// </summary>
    Task<ApiResult<RestoreStatusResponse?>> RestoreStatusAsync();
}

/// <inheritdoc cref="IDatabaseApi"/>
public sealed class DatabaseApi(HttpClient http) : IDatabaseApi
{
    public async Task<ApiResult<DatabaseInfoResponse>> InfoAsync()
    {
        try
        {
            using var response = await http.GetAsync(new Uri("api/database/info", UriKind.Relative));
            if (response.IsSuccessStatusCode)
            {
                var info = await response.Content.ReadFromJsonAsync<DatabaseInfoResponse>(ApiCalls.Json);
                return info is null
                    ? ApiResult.Failure<DatabaseInfoResponse>(response.StatusCode, string.Empty)
                    : ApiResult.Success(info, response.StatusCode);
            }

            return ApiResult.Failure<DatabaseInfoResponse>(response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            return ApiResult.Failure<DatabaseInfoResponse>(null, string.Empty);
        }
    }

    public async Task<ApiResult<RestoreStatusResponse?>> RestoreStatusAsync()
    {
        try
        {
            using var response = await http.GetAsync(new Uri("api/database/restore/status", UriKind.Relative));
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return ApiResult.Success<RestoreStatusResponse?>(null, response.StatusCode);
            }

            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<RestoreStatusResponse>(ApiCalls.Json);
                return status is null
                    ? ApiResult.Failure<RestoreStatusResponse?>(response.StatusCode, string.Empty)
                    : ApiResult.Success<RestoreStatusResponse?>(status, response.StatusCode);
            }

            return ApiResult.Failure<RestoreStatusResponse?>(response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            return ApiResult.Failure<RestoreStatusResponse?>(null, string.Empty);
        }
    }
}
