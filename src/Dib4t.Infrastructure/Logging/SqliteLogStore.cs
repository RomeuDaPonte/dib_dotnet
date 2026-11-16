using Microsoft.Data.Sqlite;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// The log store file (PLAN.md §3.3): <c>logs</c> table with its indexes, an external-content FTS5 index over message,
/// exception and properties, and a one-row <c>meta</c> table. Not in MongoDB, so a restore never touches it. One
/// long-lived connection, used only by the sink's background writer.
/// </summary>
internal sealed class SqliteLogStore : ILogBatchWriter
{
    /// <summary>2: index on <c>(user_name, timestamp_utc)</c> for the Registos user filter and list.</summary>
    public const int SchemaVersion = 2;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS logs (
            id INTEGER PRIMARY KEY,
            timestamp_utc INTEGER NOT NULL,
            level TEXT NOT NULL,
            category TEXT NOT NULL,
            source_context TEXT,
            message TEXT NOT NULL,
            exception TEXT,
            properties_json TEXT,
            request_id TEXT,
            user_name TEXT);
        CREATE INDEX IF NOT EXISTS ix_logs_timestamp ON logs (timestamp_utc);
        CREATE INDEX IF NOT EXISTS ix_logs_level_timestamp ON logs (level, timestamp_utc);
        CREATE INDEX IF NOT EXISTS ix_logs_category_timestamp ON logs (category, timestamp_utc);
        CREATE INDEX IF NOT EXISTS ix_logs_request_id ON logs (request_id);
        CREATE INDEX IF NOT EXISTS ix_logs_user_timestamp ON logs (user_name, timestamp_utc);
        CREATE VIRTUAL TABLE IF NOT EXISTS logs_fts USING fts5 (
            message, exception, properties_json, content = 'logs', content_rowid = 'id');
        CREATE TRIGGER IF NOT EXISTS logs_ai AFTER INSERT ON logs BEGIN
            INSERT INTO logs_fts (rowid, message, exception, properties_json)
            VALUES (new.id, new.message, new.exception, new.properties_json);
        END;
        CREATE TRIGGER IF NOT EXISTS logs_ad AFTER DELETE ON logs BEGIN
            INSERT INTO logs_fts (logs_fts, rowid, message, exception, properties_json)
            VALUES ('delete', old.id, old.message, old.exception, old.properties_json);
        END;
        CREATE TABLE IF NOT EXISTS meta (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            schema_version INTEGER NOT NULL,
            last_cleanup_utc INTEGER);
        INSERT OR IGNORE INTO meta (id, schema_version) VALUES (1, 2);
        UPDATE meta SET schema_version = 2 WHERE schema_version < 2;
        """;

    private const string InsertSql = """
        INSERT INTO logs (timestamp_utc, level, category, source_context, message, exception, properties_json, request_id, user_name)
        VALUES ($timestamp, $level, $category, $source, $message, $exception, $properties, $requestId, $userName)
        """;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly SqliteParameter[] _parameters;

    public SqliteLogStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Path = path;
        _connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate);

        // auto_vacuum only takes effect before the first table is created (new file); WAL is persistent.
        Execute("PRAGMA auto_vacuum = INCREMENTAL;");
        Execute("PRAGMA journal_mode = WAL;");
        Execute(Schema);

        _insert = _connection.CreateCommand();
        _insert.CommandText = InsertSql;
        _parameters =
        [
            _insert.Parameters.Add("$timestamp", SqliteType.Integer),
            _insert.Parameters.Add("$level", SqliteType.Text),
            _insert.Parameters.Add("$category", SqliteType.Text),
            _insert.Parameters.Add("$source", SqliteType.Text),
            _insert.Parameters.Add("$message", SqliteType.Text),
            _insert.Parameters.Add("$exception", SqliteType.Text),
            _insert.Parameters.Add("$properties", SqliteType.Text),
            _insert.Parameters.Add("$requestId", SqliteType.Text),
            _insert.Parameters.Add("$userName", SqliteType.Text),
        ];
    }

    public string Path { get; }

    /// <summary>All entries in one transaction, with the prepared insert.</summary>
    public void Write(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        _insert.Transaction = transaction;
        foreach (var entry in entries)
        {
            _parameters[0].Value = entry.TimestampUtcMs;
            _parameters[1].Value = entry.Level;
            _parameters[2].Value = entry.Category;
            _parameters[3].Value = (object?)entry.SourceContext ?? DBNull.Value;
            _parameters[4].Value = entry.Message;
            _parameters[5].Value = (object?)entry.Exception ?? DBNull.Value;
            _parameters[6].Value = (object?)entry.PropertiesJson ?? DBNull.Value;
            _parameters[7].Value = (object?)entry.RequestId ?? DBNull.Value;
            _parameters[8].Value = (object?)entry.UserName ?? DBNull.Value;
            _insert.ExecuteNonQuery();
        }

        transaction.Commit();
        _insert.Transaction = null;
    }

    /// <summary>
    /// A connection to the store with the per-connection settings (the writer's, and the separate ones of the Registos
    /// queries and the cleanup job: WAL lets them read while the writer writes; a writer waits up to 5 s for another).
    /// </summary>
    internal static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA cache_size = -8192; PRAGMA busy_timeout = 5000;"; // 8 MB cache
        command.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        _insert.Dispose();
        _connection.Dispose();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
