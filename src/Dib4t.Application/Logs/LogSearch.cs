namespace Dib4t.Application.Logs;

/// <summary>
/// The raw query of <c>GET /api/logs</c> (PLAN.md §3.3, Registos tab), as the request carries it. <see cref="Levels"/>
/// and <see cref="Categories"/> hold every value given (repeated parameters, each may be a comma-separated list).
/// </summary>
public sealed record LogSearchInput(
    string? From,
    string? To,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Categories,
    string? User,
    string? RequestId,
    string? Text,
    string? Cursor,
    string? Limit);

/// <summary>
/// A validated log search: entries with <c>From ≤ timestamp ≤ To</c> that match every filter given, newest first,
/// after <see cref="After"/> (keyset paging on <c>(timestamp, id)</c>), at most <see cref="Limit"/>.
/// <see cref="FullTextQuery"/> is an FTS5 query built by <see cref="LogFullTextQuery"/>, never the user's raw text.
/// </summary>
public sealed record LogSearch(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Categories,
    string? User,
    string? RequestId,
    string? FullTextQuery,
    LogCursor? After,
    int Limit);

/// <summary>Position after the last entry of a page: entries older than it come next (newest first).</summary>
public readonly record struct LogCursor(long TimestampUtcMs, long Id)
{
    public override string ToString() => FormattableString.Invariant($"{TimestampUtcMs}-{Id}");

    /// <summary>The text form <c>{timestamp ms}-{id}</c> of <see cref="ToString"/>.</summary>
    public static bool TryParse(string? text, out LogCursor cursor)
    {
        cursor = default;
        var dash = text?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        if (dash <= 0
            || !long.TryParse(text.AsSpan(0, dash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timestamp)
            || !long.TryParse(text.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        cursor = new LogCursor(timestamp, id);
        return true;
    }
}

/// <summary>One entry of the log store, as Registos shows it.</summary>
public sealed record LogRecord(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string? SourceContext,
    string Message,
    string? Exception,
    string? PropertiesJson,
    string? RequestId,
    string? UserName);

/// <summary>A page of results; <see cref="Next"/> is set when older entries may follow.</summary>
public sealed record LogPage(IReadOnlyList<LogRecord> Items, LogCursor? Next);

/// <summary>Values for the Registos filter lists.</summary>
public sealed record LogFacets(IReadOnlyList<string> Categories, IReadOnlyList<string> Levels, IReadOnlyList<string> Users);

/// <summary>Reads the log store (PLAN.md §3.3): never MongoDB, so browsing the logs costs the business database nothing.</summary>
public interface ILogQueryService
{
    Task<LogPage> SearchAsync(LogSearch search, CancellationToken cancellationToken);

    /// <summary>Every user name that has entries, sorted.</summary>
    Task<IReadOnlyList<string>> UsersAsync(CancellationToken cancellationToken);
}
