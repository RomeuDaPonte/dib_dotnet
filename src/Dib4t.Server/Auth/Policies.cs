using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Server.Auth;

/// <summary>
/// The auth columns of PLAN.md §6: <b>T</b> = <c>[Authorize]</c> (a valid token whose user still exists),
/// <b>S</b> = <c>[Authorize(Policy = Policies.CanSeeDefinicoes)]</c> (legacy <c>authCanSeeSettings</c>).
/// </summary>
public static class Policies
{
    /// <summary>The user's current roles include <see cref="RoleNames.CanSeeDefinicoes"/>.</summary>
    public const string CanSeeDefinicoes = "CanSeeDefinicoes";
}
