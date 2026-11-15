namespace Dib4t.Application.Account;

/// <summary>Configuration section <c>Auth</c> (PLAN.md §4.6).</summary>
public sealed class UserSessionOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// How long the user behind a token is kept in memory before the database is read again: a removed role or a
    /// deleted user takes effect within this time, without a new login.
    /// </summary>
    public TimeSpan UserCacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}
