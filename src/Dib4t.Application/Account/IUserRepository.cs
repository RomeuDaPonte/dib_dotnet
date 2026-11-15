using Dib4t.Domain.Users;

namespace Dib4t.Application.Account;

/// <summary>Reads of the <c>users</c> collection (legacy <c>repositories/userRepository.js</c>).</summary>
public interface IUserRepository
{
    /// <summary>
    /// Legacy <c>User.findOne({ email })</c>: exact, case-sensitive match; with duplicates, the first in natural order.
    /// </summary>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>The user with this <c>_id</c> (24 hex digits); <c>null</c> when there is none or the id is not an ObjectId.</summary>
    Task<User?> FindByIdAsync(string id, CancellationToken cancellationToken);
}
