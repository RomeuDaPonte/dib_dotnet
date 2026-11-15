using System.Text.Json;
using Dib4t.Client.Auth;
using Dib4t.Contracts.Account;

namespace Dib4t.Client.Api;

/// <inheritdoc cref="IAccountApi"/>
public sealed class AccountApi(HttpClient http) : IAccountApi
{
    /// <summary>Legacy axios interceptor text for errors that are not 4xx (network, 5xx).</summary>
    public const string UnexpectedError = ErrorNotificationHandler.UnexpectedError;

    public async Task<ApiResult<string>> LoginAsync(string email, string password)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/account/login", new LoginRequest(email, password));
            var text = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode
                ? ApiResult.Success(text, response.StatusCode)
                : ApiResult.Failure<string>(response.StatusCode, ErrorNotificationHandler.IsExpectedError((int)response.StatusCode) ? text : UnexpectedError);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Failure<string>(null, UnexpectedError);
        }
    }

    public async Task<ApiResult<SessionUser>> GetMeAsync()
    {
        try
        {
            using var response = await http.GetAsync(new Uri("api/account/me", UriKind.Relative));
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult.Failure<SessionUser>(response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var user = SessionUser.FromClaims(await response.Content.ReadFromJsonAsync<JsonElement>());
            return user is null
                ? ApiResult.Failure<SessionUser>(response.StatusCode, UnexpectedError)
                : ApiResult.Success(user);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return ApiResult.Failure<SessionUser>(null, UnexpectedError);
        }
    }
}
