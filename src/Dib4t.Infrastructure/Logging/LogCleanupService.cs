using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// The daily log cleanup (PLAN.md §3.3), inside the app: every day at <see cref="LogCleanupOptions.At"/> (02:00) in
/// <see cref="LogCleanupOptions.TimeZone"/> (Europe/Lisbon) it deletes the entries older than
/// <see cref="LogsOptions.RetentionMonths"/> calendar months, in chunks, then the oldest ones while the data is larger
/// than <see cref="LogsOptions.MaxSizeMB"/>, gives the space back (incremental vacuum, WAL checkpoint) and records the
/// run in the <c>meta</c> table. <b>Catch-up:</b> at startup it runs once when the last successful cleanup is more than
/// 24 h old (or there was none). A failure is logged as an Error; the next try is at the next 02:00.
/// Log category <c>Jobs</c>. Times come from <see cref="TimeProvider"/>, so tests move the clock.
/// </summary>
internal sealed partial class LogCleanupService(
    LogStoreLocation location,
    IOptions<LogsOptions> options,
    TimeProvider time,
    ILogger<LogCleanupService> logger) : BackgroundService
{
    /// <summary>A cleanup older than this at startup is caught up at once.</summary>
    public static TimeSpan CatchUpAfter { get; } = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _running = new(1, 1);
    private int _runs;

    /// <summary>Completed runs (successful or not) since start: lets tests wait for a scheduled run.</summary>
    public int Runs => Volatile.Read(ref _runs);

    public override void Dispose()
    {
        _running.Dispose();
        base.Dispose();
    }

    /// <summary>One cleanup now. <see langword="false"/> when it failed (logged).</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            return Run(cancellationToken);
        }
        finally
        {
            _running.Release();
            Interlocked.Increment(ref _runs);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never on the host's start path: the SQLite calls are synchronous
        var settings = options.Value;
        var zone = settings.Cleanup.FindTimeZone()!; // checked at startup (ValidateOnStart)
        try
        {
            if (LastCleanup() is not { } last || time.GetUtcNow() - last > CatchUpAfter)
            {
                LogCatchUp();
                await RunOnceAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                var next = LogCleanupSchedule.NextRun(now, zone, settings.Cleanup.At);
                await Task.Delay(next - now, time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    private DateTimeOffset? LastCleanup()
    {
        try
        {
            using var store = new LogStoreMaintenance(location.Path);
            return store.LastCleanup;
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            LogFailed(exception);
            return null;
        }
    }

    private bool Run(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var zone = settings.Cleanup.FindTimeZone()!;
        var started = time.GetTimestamp();
        var now = time.GetUtcNow();
        var cutoff = LogCleanupSchedule.Cutoff(now, zone, settings.RetentionMonths);
        LogStarted(cutoff);
        try
        {
            using var store = new LogStoreMaintenance(location.Path);
            var sizeBefore = store.FileBytes;
            var chunk = settings.Cleanup.ChunkSize;

            long deleted = 0;
            int count;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = store.DeleteOlderThan(cutoff.ToUnixTimeMilliseconds(), chunk);
                deleted += count;
            }
            while (count == chunk);

            long deletedForSize = 0;
            var maxBytes = settings.MaxSizeMB * 1024L * 1024L;
            while (store.UsedBytes > maxBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = store.DeleteOldest(chunk);
                if (count == 0)
                {
                    break;
                }

                deletedForSize += count;
            }

            store.Reclaim();
            store.SetLastCleanup(now);
            var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
            var sizeAfter = Megabytes(store.FileBytes);
            var before = Megabytes(sizeBefore);
            LogFinished(deleted, cutoff, elapsed, before, sizeAfter);
            if (deletedForSize > 0)
            {
                LogSizeGuard(deletedForSize, settings.MaxSizeMB);
            }

            return true;
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            LogFailed(exception);
            return false;
        }
    }

    private static double Megabytes(long bytes) => Math.Round(bytes / (1024d * 1024d), 2);

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Log cleanup: last run more than 24 h ago, catching up")]
    private partial void LogCatchUp();

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Log cleanup started: entries before {Cutoff:o}")]
    private partial void LogStarted(DateTimeOffset cutoff);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "Log cleanup finished: {Deleted} entries before {Cutoff:o} deleted in {ElapsedMs:0.0} ms; file {SizeBeforeMB} MB → {SizeAfterMB} MB")]
    private partial void LogFinished(long deleted, DateTimeOffset cutoff, double elapsedMs, double sizeBeforeMB, double sizeAfterMB);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning,
        Message = "Log cleanup: {Deleted} more of the oldest entries deleted to keep the log store under {MaxSizeMB} MB")]
    private partial void LogSizeGuard(long deleted, int maxSizeMB);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Log cleanup failed; next try at the next scheduled time")]
    private partial void LogFailed(Exception exception);
}
