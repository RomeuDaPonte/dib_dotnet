using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dib4t.Server.Tests.Settings;

/// <summary>Small HTTP helpers for the legacy contract tests: raw JSON bodies, the token header, compact JSON.</summary>
internal static class LegacyHttp
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static async Task<(int Status, string ContentType, string Text)> SendAsync(
        HttpClient client, HttpMethod method, string path, string? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("x-auth-token", token);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The JSON text in one canonical form (key order and number text kept), so two answers compare as text.</summary>
    public static string Normalize(string json) => JsonSerializer.Serialize(JsonElement.Parse(json), Compact);

    public static string Normalize(JsonElement json) => JsonSerializer.Serialize(json, Compact);
}
