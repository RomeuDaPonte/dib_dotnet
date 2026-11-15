using System.Collections.Concurrent;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Options;

namespace Dib4t.Application.Account;

/// <summary>
/// The users behind tokens, kept for <see cref="UserSessionOptions.UserCacheDuration"/> (PLAN.md §4.6: roles come from
/// the database, not from the token, which never expires). "No such user" is cached too, so a deleted user's token
/// does not reach the database on every request. Keys are ids from validly signed tokens only, so the cache holds at
/// most one entry per user. Singleton.
/// </summary>
public sealed class UserSessionCache(IOptions<UserSessionOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The cached user, or the one <paramref name="load"/> returns (then cached).</summary>
    public async Task<User?> GetOrLoadAsync(string userId, Func<CancellationToken, Task<User?>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(userId, out var entry) && entry.ExpiresAt > now)
        {
            return entry.User;
        }

        var user = await load(cancellationToken);
        _entries[userId] = new Entry(user, now + options.Value.UserCacheDuration);
        return user;
    }

    /// <summary>Forgets one user (changed or deleted by an administrator): the next request reads the database.</summary>
    public void Remove(string userId) => _entries.TryRemove(userId, out _);

    /// <summary>Forgets every user (e.g. after the database was replaced by a restore).</summary>
    public void Clear() => _entries.Clear();

    private sealed record Entry(User? User, DateTimeOffset ExpiresAt);
}
