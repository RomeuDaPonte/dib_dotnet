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
}
