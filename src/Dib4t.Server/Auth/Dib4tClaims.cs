using System.Security.Claims;
using Dib4t.Domain.Users;

namespace Dib4t.Server.Auth;

/// <summary>
/// The principal of an authenticated request: built from the <b>database</b> user behind the token, not from the
/// token's claims (PLAN.md §4.6), with the legacy claim names.
/// </summary>
public static class Dib4tClaims
{
    public const string AuthenticationType = "x-auth-token";
    public const string UserId = "_id";
    public const string Name = "name";
    public const string Email = "email";
    public const string Role = "roles";
    public const string FieldOfCompetencies = "fieldOfCompetencies";

    public static ClaimsPrincipal ToPrincipal(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claims = new List<Claim> { new(UserId, user.Id), new(FieldOfCompetencies, user.FieldOfCompetencies) };
        // The log store's user column (PLAN.md §3.3) is the identity name: the name, else the e-mail.
        if ((user.Name ?? user.Email) is { } name)
        {
            claims.Add(new Claim(Name, name));
        }

        if (user.Email is not null)
        {
            claims.Add(new Claim(Email, user.Email));
        }

        claims.AddRange(user.EffectiveRoles.Select(role => new Claim(Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, Name, Role));
    }

    /// <summary>The <c>_id</c> of the signed-in user.</summary>
    public static string? GetUserId(this ClaimsPrincipal principal) => principal?.FindFirst(UserId)?.Value;
}
