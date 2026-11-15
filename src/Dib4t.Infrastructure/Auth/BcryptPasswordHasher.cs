using System.Security.Cryptography;
using System.Text;
using Dib4t.Application.Account;

namespace Dib4t.Infrastructure.Auth;

/// <summary>
/// bcrypt with the semantics of the legacy <b>bcryptjs 2.4.3</b> (PLAN.md §4.6), on BCrypt.Net-Next. Checked against
/// hashes made by bcryptjs itself (<c>tests/fixtures/legacy/auth/auth-golden.json</c>): UTF-8 passwords, the 72-byte
/// limit, <c>$2a$</c> and <c>$2b$</c>, any cost.
/// </summary>
internal sealed class BcryptPasswordHasher : IPasswordHasher
{
    /// <summary>bcryptjs <c>genSalt(10)</c>, the cost the legacy app writes.</summary>
    public const int WorkFactor = 10;

    /// <summary>
    /// bcryptjs <c>compareSync</c>: a hash that is not 60 characters long is "no match"; otherwise the password is
    /// hashed with the hash's own salt and compared in constant time. A 60-character string with an invalid salt
    /// throws, like bcryptjs (the request then fails with 500, as in the legacy app).
    /// </summary>
    public bool Verify(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 60)
        {
            return false;
        }

        var computed = BCrypt.Net.BCrypt.HashPassword(password, hash[..29]);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(hash));
    }

    /// <summary>bcryptjs <c>hash(password, genSalt(10))</c>: <c>$2a$10$</c> + 53 characters.</summary>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(WorkFactor, 'a'));
    }
}
