using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dib4t.Application.Tests.Account;

/// <summary>
/// The users behind tokens (PLAN.md §4.6), and the freeze a restore holds while it replaces the collections (§7.6):
/// a page open meanwhile keeps its signed-in user although <c>users</c> is empty.
/// </summary>
public sealed class UserSessionCacheTests
{
    private const string Id = "5f1e9a2b3c4d5e6f70819203";
    private static readonly User Ana = new(Id, "Ana", "ana@dib4t.local", "hash", ["canSeeObras"], "");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly UserSessionCache _cache;
    private User? _stored = Ana;
    private int _loads;

    public UserSessionCacheTests() => _cache = new UserSessionCache(Options.Create(new UserSessionOptions()), _time);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private Task<User?> GetAsync() => _cache.GetOrLoadAsync(Id, _ => { _loads++; return Task.FromResult(_stored); }, Cancellation);

    [Fact]
    public async Task A_user_is_read_again_once_the_entry_expired()
    {
        await GetAsync();
        _time.Advance(TimeSpan.FromSeconds(29));
        await GetAsync();
        _loads.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(2));
        _stored = null;
        (await GetAsync()).Should().BeNull("deleted meanwhile");
        await GetAsync();
        _loads.Should().Be(2, "a missing user is remembered too");
    }

    [Fact]
    public async Task While_frozen_a_known_user_stays_although_expired_and_the_database_has_none()
    {
        await GetAsync();
        _stored = null; // the restore dropped users

        using (_cache.Freeze())
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            (await GetAsync()).Should().Be(Ana);
            _loads.Should().Be(1);
        }

        (await GetAsync()).Should().BeNull("after the freeze the entry expires as usual");
    }

    [Fact]
    public async Task While_frozen_a_user_not_found_is_not_remembered()
    {
        _stored = null;
        using (_cache.Freeze())
        {
            (await GetAsync()).Should().BeNull();
            _stored = Ana; // inserted by the restore a moment later
            (await GetAsync()).Should().Be(Ana);
        }

        _loads.Should().Be(2);
    }

    [Fact]
    public async Task Disposing_a_freeze_twice_does_not_end_another_one()
    {
        await GetAsync();
        _stored = null;
        using var outer = _cache.Freeze();
        var inner = _cache.Freeze();
        inner.Dispose();
        inner.Dispose();

        _time.Advance(TimeSpan.FromMinutes(1));
        (await GetAsync()).Should().Be(Ana, "the outer freeze still holds");
    }
}
