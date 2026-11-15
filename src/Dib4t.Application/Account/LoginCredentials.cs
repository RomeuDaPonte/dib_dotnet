namespace Dib4t.Application.Account;

/// <summary>A login body that passed the legacy validation.</summary>
public sealed record LoginCredentials(string Email, string Password)
{
    /// <summary>Never print the password (records include every property in <c>ToString</c>).</summary>
    public override string ToString() => $"LoginCredentials {{ Email = {Email} }}";
}
