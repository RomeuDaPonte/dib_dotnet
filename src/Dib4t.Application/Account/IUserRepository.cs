using Dib4t.Domain.Users;

namespace Dib4t.Application.Account;

/// <summary>The <c>users</c> collection (legacy <c>repositories/userRepository.js</c>).</summary>
public interface IUserRepository
{
    /// <summary>
    /// Legacy <c>User.findOne({ email })</c>: exact, case-sensitive match; with duplicates, the first in natural order.
    /// </summary>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>The user with this <c>_id</c> (24 hex digits); <c>null</c> when there is none or the id is not an ObjectId.</summary>
    Task<User?> FindByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>Legacy <c>getAll</c>: every user, sorted by <c>name</c> (MongoDB order: users without a name first).</summary>
    Task<IReadOnlyList<User>> ListByNameAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a user in the legacy shape (field order, <c>__v: 0</c>); returns its new <c>_id</c>.</summary>
    Task<string> InsertAsync(NewUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>user.update(body)</c> + <c>save()</c>: writes only what differs from the stored document (<c>$set</c>,
    /// <c>$unset roles</c>; <c>$inc __v</c> when the roles array is replaced, PLAN.md §4.3 rules 3–4); nothing at all
    /// when nothing differs. <c>false</c> when the user does not exist (any more).
    /// </summary>
    Task<bool> UpdateAsync(string id, UserUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes the user with this <c>_id</c> (D2 fixed: this one, not the first user); <c>false</c> when none was deleted.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>A user to insert (legacy <c>POST /api/account/create</c> after the Mongoose casts).</summary>
public sealed record NewUser(string Name, string Email, string PasswordHash, IReadOnlyList<string?> Roles, string FieldOfCompetencies)
{
    /// <summary>Never print the hash.</summary>
    public override string ToString() => $"NewUser {{ Name = {Name}, Email = {Email} }}";
}

/// <summary>
/// The values legacy <c>user.update(body)</c> assigns: name, e-mail and field of competencies always; the roles, or
/// none (<see cref="Roles"/> <c>null</c> = the body had no roles, so the stored array is removed); and a new password
/// hash when the body had a password.
/// </summary>
public sealed record UserUpdate(string Name, string Email, string FieldOfCompetencies, IReadOnlyList<string?>? Roles, string? PasswordHash)
{
    /// <summary>Never print the hash.</summary>
    public override string ToString() => $"UserUpdate {{ Name = {Name}, Email = {Email}, FieldOfCompetencies = {FieldOfCompetencies} }}";
}
