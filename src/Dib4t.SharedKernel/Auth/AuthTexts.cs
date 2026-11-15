namespace Dib4t.SharedKernel.Auth;

/// <summary>
/// The legacy auth contract (PLAN.md §4.6): the token header and the exact response texts of
/// <c>middleware/auth.js</c> and <c>middleware/authCanSeeSettings.js</c>. Shared by the server (responses) and the
/// client (an <see cref="InvalidToken"/> answer logs the user out).
/// </summary>
public static class AuthTexts
{
    /// <summary>Request header that carries the JWT (no <c>Bearer</c> prefix).</summary>
    public const string TokenHeader = "x-auth-token";

    /// <summary>401: no token, or an empty <see cref="TokenHeader"/>.</summary>
    public const string NoToken = "Access denied. No token provided.";

    /// <summary>400: bad signature, malformed token, or the token's user no longer exists (§4.6).</summary>
    public const string InvalidToken = "Invalid token.";

    /// <summary>401: a valid token without the settings role (<c>canSeeDefinicoes</c>).</summary>
    public const string NoPermission = "Não tem permissão para concluir esta acção.";
}
