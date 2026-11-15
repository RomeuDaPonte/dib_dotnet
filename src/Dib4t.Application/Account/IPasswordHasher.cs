namespace Dib4t.Application.Account;

/// <summary>bcrypt as the legacy app uses it (bcryptjs, PLAN.md §4.6). Passwords and hashes are never logged.</summary>
public interface IPasswordHasher
{
    /// <summary>Legacy <c>bcrypt.compare</c>: <c>$2a$</c>/<c>$2b$</c> hashes of any cost.</summary>
    bool Verify(string password, string hash);

    /// <summary>A new hash in the legacy format: <c>$2a$10$…</c> (bcryptjs <c>genSalt(10)</c>).</summary>
    string Hash(string password);
}
