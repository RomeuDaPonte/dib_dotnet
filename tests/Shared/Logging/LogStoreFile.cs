using Microsoft.Data.Sqlite;

namespace Dib4t.Tests.Common.Logging;

/// <summary>One row of the log store (PLAN.md §3.3), read independently of the app's code.</summary>
public sealed record LogRow(
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

/// <summary>Test access to a log store file: rows, free SQL, and a temp path that cleans up after itself.</summary>
public static class LogStoreFile
{
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"dib4t-test-logs-{Guid.NewGuid():N}.db");

    public static void Delete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // still open by a host that is shutting down; it is a temp file
            }
        }
    }

    public static async Task<IReadOnlyList<LogRow>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, level, category, source_context, message, exception, properties_json, request_id, user_name
            FROM logs ORDER BY id
            """;
        var rows = new List<LogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LogRow(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetValue(4) as string,
                reader.GetString(5),
                reader.GetValue(6) as string,
                reader.GetValue(7) as string,
                reader.GetValue(8) as string,
                reader.GetValue(9) as string));
        }

        return rows;
    }

    public static async Task<object?> ScalarAsync(string path, string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Waits until the store has rows matching <paramref name="predicate"/> (the writer is asynchronous).</summary>
    public static async Task<IReadOnlyList<LogRow>> WaitForAsync(
        string path, Func<IReadOnlyList<LogRow>, bool> predicate, Func<Task>? flush = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (flush is not null)
            {
                await flush();
            }

            var rows = File.Exists(path) ? await ReadAsync(path, cancellationToken) : [];
            if (predicate(rows) || DateTime.UtcNow > deadline)
            {
                return rows;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
