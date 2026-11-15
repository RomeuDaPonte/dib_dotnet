using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Account;

/// <summary>
/// Login and the signed-in user (legacy <c>routes/account.js</c> login, PLAN.md §4.6, §6). Every outcome is logged
/// in category <c>Auth</c> with the e-mail, never the password (§3.3).
/// </summary>
public sealed partial class AccountService(
    IUserRepository users,
    IPasswordHasher passwords,
    ITokenService tokens,
    UserSessionCache sessions,
    ILogger<AccountService> logger)
{
    /// <summary>
    /// <c>POST /api/account/login</c>: the legacy order is validation (400 Joi text), unknown e-mail (400
    /// <see cref="AccountMessages.UnknownEmail"/>), wrong password (400 <see cref="AccountMessages.WrongPassword"/>),
    /// then the raw JWT. A stored user without a password hash fails like the legacy <c>bcrypt.compare</c> (500).
    /// </summary>
    public async Task<Result<string>> LoginAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var credentials = LoginValidator.Validate(body);
        if (!credentials.IsSuccess)
        {
            LogLoginRejected(credentials.Error.Message);
            return credentials.Error;
        }

        var (email, password) = credentials.Value;
        var user = await users.FindByEmailAsync(email, cancellationToken);
        if (user is null)
        {
            LogUnknownEmail(email);
            return Error.Validation(AccountMessages.UnknownEmail);
        }

        var hash = user.PasswordHash ?? throw new InvalidOperationException($"User {user.Id} has no password hash.");
        if (!passwords.Verify(password, hash))
        {
            LogWrongPassword(email, user.Id);
            return Error.Validation(AccountMessages.WrongPassword);
        }

        LogLoginSucceeded(email, user.Id);
        return tokens.CreateToken(user);
    }

    /// <summary>
    /// The user behind a validly signed token, with the current roles from the database (cached for a short time,
    /// <see cref="UserSessionCache"/>); <c>null</c> when the user no longer exists.
    /// </summary>
    public Task<User?> GetSessionUserAsync(string userId, CancellationToken cancellationToken) =>
        sessions.GetOrLoadAsync(userId, ct => users.FindByIdAsync(userId, ct), cancellationToken);

    /// <summary><c>GET /api/account/me</c>: the user document as it is now (not the cached copy).</summary>
    public Task<User?> GetUserAsync(string userId, CancellationToken cancellationToken) =>
        users.FindByIdAsync(userId, cancellationToken);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Login succeeded for {Email} (user {UserId})")]
    private partial void LogLoginSucceeded(string email, string userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Login failed for {Email}: unknown e-mail")]
    private partial void LogUnknownEmail(string email);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Login failed for {Email} (user {UserId}): wrong password")]
    private partial void LogWrongPassword(string email, string userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Login rejected: {Reason}")]
    private partial void LogLoginRejected(string reason);
}
