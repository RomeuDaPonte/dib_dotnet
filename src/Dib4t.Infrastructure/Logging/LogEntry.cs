namespace Dib4t.Infrastructure.Logging;

/// <summary>One row of the <c>logs</c> table (PLAN.md §3.3).</summary>
internal sealed record LogEntry(
    long TimestampUtcMs,
    string Level,
    string Category,
    string? SourceContext,
    string Message,
    string? Exception,
    string? PropertiesJson,
    string? RequestId,
    string? UserName);

/// <summary>Writes a batch of entries in one transaction (the SQLite store; tests substitute it).</summary>
internal interface ILogBatchWriter : IDisposable
{
    void Write(IReadOnlyList<LogEntry> entries);
}
