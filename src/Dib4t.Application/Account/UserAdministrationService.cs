using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Domain.Users;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Account;

/// <summary>
/// User management (legacy <c>routes/account.js</c>, every route <c>authCanSeeSettings</c>; PLAN.md §6, §7.2): roles,
/// list, one user, create, edit, delete. Same order of checks and texts as the legacy routes; a failure the legacy
/// app leaves to Express (cast error, refused save) is a 500 <see cref="LegacyMessages.SomethingFailed"/> (D16).
/// Changes are logged in category <c>Auth</c> (who did what to which user; never a password or hash).
/// </summary>
public sealed partial class UserAdministrationService(
    IUserRepository users,
    IPasswordHasher passwords,
    UserSessionCache sessions,
    ILogger<UserAdministrationService> logger)
{
    /// <summary><c>GET /api/account/roles</c>: legacy <c>getAllRoles()</c> (without <c>canSeeAdjudicacoes</c>, D15).</summary>
    public static IReadOnlyList<string> Roles => RoleNames.All;

    /// <summary><c>GET /api/account/users</c>.</summary>
    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken) => users.ListByNameAsync(cancellationToken);

    /// <summary><c>GET /api/account/:id</c>: 404 <see cref="AccountMessages.NotFound"/>; a non-ObjectId id is a 500 (legacy cast error).</summary>
    public async Task<Result<User>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (CastId(id) is not { } objectId)
        {
            return CastFailure(id);
        }

        var user = await users.FindByIdAsync(objectId, cancellationToken);
        return user is null ? Error.NotFound(AccountMessages.NotFound) : user;
    }

    /// <summary>
    /// <c>POST /api/account/create</c>: Joi (400) → e-mail already registered (400 <see cref="AccountMessages.EmailTaken"/>)
    /// → model casts (500) → bcrypt <c>$2a$10$</c> → insert. Answers the stored user.
    /// </summary>
    public async Task<Result<User>> CreateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = UserValidators.ValidateCreate(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        // Stored e-mails are NFC (PLAN.md §4.3 rule 2), so the duplicate check is too.
        var email = validated.Value.Email.Normalize(System.Text.NormalizationForm.FormC);
        if (await users.FindByEmailAsync(email, cancellationToken) is not null)
        {
            LogCreateRejected(email, AccountMessages.EmailTaken);
            return Error.Validation(AccountMessages.EmailTaken);
        }

        var input = validated.Value.ToInput();
        if (!input.IsSuccess)
        {
            LogNotSaved("create", email, input.Error.Reason);
            return input.Error;
        }

        var value = input.Value;
        var newUser = new NewUser(value.Name, value.Email, passwords.Hash(value.Password.Text!), value.Roles ?? [], value.FieldOfCompetencies);
        var id = await users.InsertAsync(newUser, cancellationToken);
        LogCreated(id, value.Email);
        return new User(id, newUser.Name, newUser.Email, newUser.PasswordHash, newUser.Roles, newUser.FieldOfCompetencies);
    }

    /// <summary>
    /// <c>PUT /api/account/:id</c>: Joi (400) → id cast (500) → unknown user (404 <see cref="AccountMessages.UserMissing"/>)
    /// → model casts and the password (500) → save of what changed (a password is re-hashed only when one is sent).
    /// Answers the user as saved; the user's cached session is dropped, so new roles apply on the next request.
    /// </summary>
    public async Task<Result<UserEditResult>> UpdateAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = UserValidators.ValidateEdit(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (CastId(id) is not { } objectId)
        {
            return CastFailure(id);
        }

        if (await users.FindByIdAsync(objectId, cancellationToken) is null)
        {
            return Error.NotFound(AccountMessages.UserMissing);
        }

        var input = validated.Value.ToInput();
        if (!input.IsSuccess)
        {
            LogNotSaved("edit", objectId, input.Error.Reason);
            return input.Error;
        }

        var value = input.Value;
        if (value.Password.IllegalType is { } type)
        {
            LogNotSaved("edit", objectId, $"bcrypt: Illegal arguments: {type}, string");
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        var hash = value.Password.Text is { } password ? passwords.Hash(password) : null;
        var update = new UserUpdate(value.Name, value.Email, value.FieldOfCompetencies, value.Roles, hash);
        if (!await users.UpdateAsync(objectId, update, cancellationToken))
        {
            return Error.NotFound(AccountMessages.UserMissing); // deleted meanwhile
        }

        sessions.Remove(objectId);
        LogUpdated(objectId, value.Email, hash is not null);
        return new UserEditResult(objectId, value.Name, value.Email, value.Roles);
    }

    /// <summary>
    /// <c>DELETE /api/account/:id</c> (not in the UI): id cast (500) → unknown (404 <see cref="AccountMessages.UserMissing"/>)
    /// → delete <b>this</b> user (D2: legacy deletes the first user of the collection) → answers the deleted user.
    /// </summary>
    public async Task<Result<User>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (CastId(id) is not { } objectId)
        {
            return CastFailure(id);
        }

        var user = await users.FindByIdAsync(objectId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound(AccountMessages.UserMissing);
        }

        if (!await users.DeleteAsync(objectId, cancellationToken))
        {
            return Error.Failure(LegacyMessages.ServerError);
        }

        sessions.Remove(objectId);
        LogDeleted(objectId, user.Email);
        return user;
    }

    private static string? CastId(string id) => LegacyObjectId.Cast(id);

    private Error CastFailure(string id)
    {
        LogCastFailed(id);
        return Error.Failure(LegacyMessages.SomethingFailed);
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "User {UserId} ({Email}) created")]
    private partial void LogCreated(string userId, string email);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "User {UserId} ({Email}) updated (password changed: {PasswordChanged})")]
    private partial void LogUpdated(string userId, string email, bool passwordChanged);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "User {UserId} ({Email}) deleted")]
    private partial void LogDeleted(string userId, string? email);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "User not created for {Email}: {Reason}")]
    private partial void LogCreateRejected(string email, string reason);

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "User {Action} not saved ({Subject}): {Reason}")]
    private partial void LogNotSaved(string action, string subject, string? reason);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Not a user id: {Id} (legacy cast error, 500)")]
    private partial void LogCastFailed(string id);
}

/// <summary>
/// The legacy answer of an edit: <c>_.pick(user, ["_id", "name", "email", "roles"])</c> after the save.
/// <see cref="Roles"/> is <c>null</c> when the edit removed the roles (left out of the answer, like legacy).
/// </summary>
public sealed record UserEditResult(string Id, string Name, string Email, IReadOnlyList<string?>? Roles);
