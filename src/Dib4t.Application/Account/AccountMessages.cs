namespace Dib4t.Application.Account;

/// <summary>The legacy texts of <c>routes/account.js</c> (PLAN.md §6), verbatim.</summary>
public static class AccountMessages
{
    public const string UnknownEmail = "Invalid email or password.";
    public const string WrongPassword = "Password incorreta!";
    public const string EmailTaken = "Email já registado.";

    /// <summary><c>GET /api/account/:id</c> of an unknown user.</summary>
    public const string NotFound = "not found!";

    /// <summary><c>PUT</c> / <c>DELETE /api/account/:id</c> of an unknown user (the legacy typo kept).</summary>
    public const string UserMissing = "Utilizador não exista na bd";
}
