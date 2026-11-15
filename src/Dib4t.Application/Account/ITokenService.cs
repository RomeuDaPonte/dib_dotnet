using Dib4t.Domain.Users;

namespace Dib4t.Application.Account;

/// <summary>Issues the login token (legacy <c>user.generateAuthToken()</c>, PLAN.md §4.6).</summary>
public interface ITokenService
{
    /// <summary>
    /// HS256 JWT with the legacy claims <c>{ _id, name, email, roles, fieldOfCompetencies, iat }</c> and no expiry,
    /// signed with the new app's own key.
    /// </summary>
    string CreateToken(User user);
}
