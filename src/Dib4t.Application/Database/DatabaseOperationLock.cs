namespace Dib4t.Application.Database;

/// <summary>
/// Only one backup or restore runs at a time (legacy <c>busyWith</c> in <c>routes/database.js</c>, PLAN.md §7.6).
/// Registered as a singleton: the lock is per app process, like the legacy module variable.
/// </summary>
public sealed class DatabaseOperationLock
{
    public const string Backup = "backup";
    public const string Restore = "restauro";

    private string? _busyWith;

    /// <summary>The running operation ("backup" or "restauro"), or <see langword="null"/> (shown by <c>GET /api/database/info</c>).</summary>
    public string? BusyWith => Volatile.Read(ref _busyWith);

    /// <summary>Takes the lock for <paramref name="operation"/>; returns <see langword="null"/> if another operation holds it.</summary>
    public IDisposable? TryAcquire(string operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        return Interlocked.CompareExchange(ref _busyWith, operation, null) is null ? new Releaser(this) : null;
    }

    private sealed class Releaser(DatabaseOperationLock owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Volatile.Write(ref owner._busyWith, null);
            }
        }
    }
}
