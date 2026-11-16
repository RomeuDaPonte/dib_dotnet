using System.Text.Json;

namespace Dib4t.Client.Api;

/// <summary>
/// One JSON call of a typed API client: the value of a 2xx answer, the legacy text of a 4xx answer (shown by the
/// caller), or <see cref="ErrorNotificationHandler.UnexpectedError"/> for anything else (already shown, §8.2).
/// </summary>
internal static class ApiCalls
{
    /// <summary>The JSON options of <c>ReadFromJsonAsync</c> (web defaults).</summary>
    public static JsonSerializerOptions Json { get; } = JsonSerializerOptions.Web;

    public static Task<ApiResult<T>> SendAsync<T>(HttpClient http, HttpMethod method, string path, object? body = null) =>
        SendAsync<T>(http, method, path, body, raw: false);

    /// <summary>
    /// With <c>raw</c>, the 2xx body as text (<typeparamref name="T"/> is <see cref="string"/>), for routes that answer
    /// JSON or plain text.
    /// </summary>
    public static async Task<ApiResult<T>> SendAsync<T>(HttpClient http, HttpMethod method, string path, object? body, bool raw)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var value = raw ? (T)(object)await response.Content.ReadAsStringAsync() : await response.Content.ReadFromJsonAsync<T>(Json);
                return value is null
                    ? ApiResult.Failure<T>(response.StatusCode, ErrorNotificationHandler.UnexpectedError)
                    : ApiResult.Success(value, response.StatusCode);
            }

            var text = await response.Content.ReadAsStringAsync();
            return ApiResult.Failure<T>(response.StatusCode,
                ErrorNotificationHandler.IsExpectedError((int)response.StatusCode) ? text : ErrorNotificationHandler.UnexpectedError);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return ApiResult.Failure<T>(null, ErrorNotificationHandler.UnexpectedError);
        }
    }
}
