namespace Dib4t.Contracts.Logs;

/// <summary>One log store entry (<c>GET /api/logs</c>, new-app only, PLAN.md §3.3). <see cref="Timestamp"/> is UTC.</summary>
public sealed record LogEntryResponse(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string? SourceContext,
    string Message,
    string? Exception,
    string? Properties,
    string? RequestId,
    string? UserName);

/// <summary>A page, newest first; <see cref="NextCursor"/> (for <c>cursor=</c>) is set when older entries may follow.</summary>
public sealed record LogPageResponse(IReadOnlyList<LogEntryResponse> Items, string? NextCursor);

/// <summary><c>GET /api/logs/facets</c>: the values of the Registos filter lists.</summary>
public sealed record LogFacetsResponse(IReadOnlyList<string> Categories, IReadOnlyList<string> Levels, IReadOnlyList<string> Users);
