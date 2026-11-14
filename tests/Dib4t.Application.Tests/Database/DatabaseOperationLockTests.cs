using Dib4t.Application.Database;

namespace Dib4t.Application.Tests.Database;

public sealed class DatabaseOperationLockTests
{
    private readonly DatabaseOperationLock _lock = new();

    [Fact]
    public void Free_lock_is_taken_and_shows_the_operation()
    {
        using var held = _lock.TryAcquire(DatabaseOperationLock.Restore);

        held.Should().NotBeNull();
        _lock.BusyWith.Should().Be("restauro");
    }

    [Fact]
    public void Held_lock_refuses_a_second_operation()
    {
        using var backup = _lock.TryAcquire(DatabaseOperationLock.Backup);

        _lock.TryAcquire(DatabaseOperationLock.Restore).Should().BeNull();
        _lock.BusyWith.Should().Be("backup");
    }

    [Fact]
    public void Released_lock_can_be_taken_again()
    {
        _lock.TryAcquire(DatabaseOperationLock.Backup)!.Dispose();

        _lock.BusyWith.Should().BeNull();
        using var again = _lock.TryAcquire(DatabaseOperationLock.Restore);
        again.Should().NotBeNull();
    }

    [Fact]
    public void Disposing_a_releaser_twice_does_not_release_a_later_holder()
    {
        var first = _lock.TryAcquire(DatabaseOperationLock.Backup)!;
        first.Dispose();
        using var second = _lock.TryAcquire(DatabaseOperationLock.Restore);

        first.Dispose();

        _lock.BusyWith.Should().Be("restauro");
    }

    [Fact]
    public void Only_one_of_many_concurrent_callers_gets_the_lock()
    {
        var held = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        Parallel.For(0, 64, _ =>
        {
            if (_lock.TryAcquire(DatabaseOperationLock.Restore) is { } releaser)
            {
                held.Add(releaser);
            }
        });

        held.Should().ContainSingle();
    }
}
