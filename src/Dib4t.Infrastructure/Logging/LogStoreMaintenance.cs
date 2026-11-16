using Microsoft.Data.Sqlite;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// The cleanup job's operations on the log store (PLAN.md §3.3), on its own connection. Deletes run in small
/// transactions (one chunk each), so the sink's writer, which waits up to 5 s for the lock, is never blocked for long.
/// The FTS index follows through the delete trigger.
/// </summary>
internal sealed class LogStoreMaintenance : IDisposable
{
    private readonly SqliteConnection _connection;

    public LogStoreMaintenance(string path)
    {
        Path = path;
        _connection = SqliteLogStore.OpenConnection(path, SqliteOpenMode.ReadWrite);
    }

    public string Path { get; }

    /// <summary>The main file's size on disk (without the WAL).</summary>
    public long FileBytes => new FileInfo(Path).Length;

    /// <summary>Bytes taken by data: all pages minus the free ones (what deleting rows reduces before a vacuum).</summary>
    public long UsedBytes =>
        (Scalar("PRAGMA page_count;") - Scalar("PRAGMA freelist_count;")) * Scalar("PRAGMA page_size;");

    /// <summary>Deletes up to <paramref name="chunk"/> of the oldest entries before <paramref name="cutoffUtcMs"/>; the number deleted.</summary>
    public int DeleteOlderThan(long cutoffUtcMs, int chunk) => Delete(
        "DELETE FROM logs WHERE id IN (SELECT id FROM logs WHERE timestamp_utc < $cutoff ORDER BY timestamp_utc LIMIT $chunk);",
        ("$cutoff", cutoffUtcMs), ("$chunk", chunk));

    /// <summary>Deletes the <paramref name="chunk"/> oldest entries (size guard); the number deleted.</summary>
    public int DeleteOldest(int chunk) => Delete(
        "DELETE FROM logs WHERE id IN (SELECT id FROM logs ORDER BY timestamp_utc, id LIMIT $chunk);", ("$chunk", chunk));

    /// <summary>Gives the free pages back to the file system and empties the WAL.</summary>
    public void Reclaim()
    {
        Execute("PRAGMA incremental_vacuum;");
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>When the last cleanup succeeded (the <c>meta</c> table), or <see langword="null"/>.</summary>
    public DateTimeOffset? LastCleanup
    {
        get
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT last_cleanup_utc FROM meta WHERE id = 1;";
            return command.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
    }

    public void SetLastCleanup(DateTimeOffset when)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE meta SET last_cleanup_utc = $when WHERE id = 1;";
        command.Parameters.AddWithValue("$when", when.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private int Delete(string sql, params (string Name, long Value)[] parameters)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var deleted = command.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader(); // the pragmas answer rows
        while (reader.Read())
        {
        }
    }
}
