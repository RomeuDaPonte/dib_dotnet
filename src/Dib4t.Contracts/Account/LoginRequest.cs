namespace Dib4t.Contracts.Account;

/// <summary><c>POST /api/account/login</c> body. The 200 answer is the raw JWT as text (legacy <c>res.send(token)</c>).</summary>
public sealed record LoginRequest(string Email, string Password)
{
    /// <summary>Never print the password.</summary>
    public override string ToString() => $"LoginRequest {{ Email = {Email} }}";
}
