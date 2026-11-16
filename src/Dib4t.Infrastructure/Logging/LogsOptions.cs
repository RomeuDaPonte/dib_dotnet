using System.ComponentModel.DataAnnotations;
using Serilog.Events;

namespace Dib4t.Infrastructure.Logging;

/// <summary>Log store and logging settings, section <c>Logs</c> (PLAN.md §3.3).</summary>
public sealed class LogsOptions
{
    public const string SectionName = "Logs";

    /// <summary>
    /// The SQLite file. A relative path is resolved against the content root. Production:
    /// <c>/app/logs/dib4t-logs.db</c> on the <c>dib4t-logs</c> volume.
    /// </summary>
    [Required]
    public string Path { get; set; } = "logs/dib4t-logs.db";

    /// <summary>Bounded in-memory queue; when it is full, new events are dropped instead of blocking the caller.</summary>
    [Range(1, 1_000_000)]
    public int QueueLimit { get; set; } = 10_000;

    /// <summary>Most events written in one transaction.</summary>
    [Range(1, 100_000)]
    public int BatchSizeLimit { get; set; } = 1_000;

    /// <summary>A batch is written at the latest this long after its first event.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan BatchPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>MongoDB commands slower than this are logged as Warning.</summary>
    [Range(1, int.MaxValue)]
    public int SlowCommandMs { get; set; } = 500;

    /// <summary>Console output (<c>docker logs</c>): Warning and up in Production keeps it small.</summary>
    public LogEventLevel ConsoleMinimumLevel { get; set; } = LogEventLevel.Information;

    /// <summary>Entries older than this many calendar months are deleted by the daily cleanup.</summary>
    [Range(1, 120)]
    public int RetentionMonths { get; set; } = 1;

    /// <summary>Size guard: when the entries take more than this, the cleanup deletes the oldest ones first.</summary>
    [Range(1, 1_000_000)]
    public int MaxSizeMB { get; set; } = 2048;

    /// <summary>The daily cleanup job (<see cref="LogCleanupService"/>).</summary>
    public LogCleanupOptions Cleanup { get; set; } = new();
}

/// <summary>Section <c>Logs:Cleanup</c> (PLAN.md §3.3): every day at <see cref="At"/> in <see cref="TimeZone"/>.</summary>
public sealed class LogCleanupOptions
{
    /// <summary>Local time of the daily run (the scheduled database backup defaults to 03:00, so they never overlap).</summary>
    public TimeOnly At { get; set; } = new(2, 0);

    /// <summary>IANA time zone of <see cref="At"/> and of the retention's calendar months (the container runs in UTC).</summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Lisbon";

    /// <summary>Rows deleted per transaction, so the log writer is never blocked for long.</summary>
    [Range(1, 1_000_000)]
    public int ChunkSize { get; set; } = 5_000;

    /// <summary>The time zone, or <see langword="null"/> when the system does not know it.</summary>
    public TimeZoneInfo? FindTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : null;
}
