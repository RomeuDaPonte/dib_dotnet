using Dib4t.Infrastructure.Logging;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>Writes rows straight into a log store file (through the real store, without the sink's queue).</summary>
internal static class LogStoreSeed
{
    public static LogEntry Entry(
        DateTimeOffset at, string message, string level = "Information", string category = "App", string? requestId = null,
        string? user = null, string? exception = null, string? properties = null, string? source = null) =>
        new(at.ToUnixTimeMilliseconds(), level, category, source, message, exception, properties, requestId, user);

    public static void Write(string path, params LogEntry[] entries)
    {
        using var store = new SqliteLogStore(path);
        store.Write(entries);
    }
}
