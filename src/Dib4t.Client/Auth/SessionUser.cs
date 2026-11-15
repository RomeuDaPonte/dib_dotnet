using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Dib4t.Client.Auth;

/// <summary>
/// The signed-in user as the UI shows it (name in the app bar, menu per role): first decoded from the token's
/// claims (legacy <c>jwtDecode</c>), then refreshed from <c>GET /api/account/me</c> (PLAN.md §4.6, §8.2).
/// </summary>
public sealed record SessionUser(string Id, string? Name, string? Email, IReadOnlyList<string> Roles, string? FieldOfCompetencies)
{
    public const string AuthenticationType = "x-auth-token";

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

    /// <summary>The token's payload, or <c>null</c> when it is not a readable JWT with an <c>_id</c>.</summary>
    public static SessionUser? FromToken(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var payload = JsonElement.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1])));
            return FromClaims(payload);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The legacy claim shape <c>{ _id, name, email, roles, fieldOfCompetencies }</c> (token or <c>/me</c>).</summary>
    public static SessionUser? FromClaims(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object
            || !claims.TryGetProperty("_id", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var roles = claims.TryGetProperty("roles", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()
            : [];
        return new SessionUser(id.GetString()!, Text(claims, "name"), Text(claims, "email"), roles, Text(claims, "fieldOfCompetencies"));
    }

    public ClaimsPrincipal ToPrincipal()
    {
        var claims = new List<Claim> { new("_id", Id) };
        if (Name is not null)
        {
            claims.Add(new Claim("name", Name));
        }

        claims.AddRange(Roles.Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, "name", "roles"));
    }

    private static string? Text(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
