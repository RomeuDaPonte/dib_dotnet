using System.Globalization;
using System.Text;
using Dib4t.Application.Logs;
using Microsoft.Data.Sqlite;

namespace Dib4t.Infrastructure.Logging;

/// <summary>The log store file's full path (resolved once from <see cref="LogsOptions.Path"/>).</summary>
internal sealed record LogStoreLocation(string Path);

/// <summary>
/// Registos queries on the SQLite log store (PLAN.md §3.3), on their own read-only connection: indexed filters,
/// FTS5 search, newest first with a keyset cursor on <c>(timestamp_utc, id)</c>, so a page never reads the whole log.
/// </summary>
internal sealed class SqliteLogQueryService(LogStoreLocation location) : ILogQueryService
{
    private const string Columns =
        "id, timestamp_utc, level, category, source_context, message, exception, properties_json, request_id, user_name";

    public async Task<LogPage> SearchAsync(LogSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        using var connection = SqliteLogStore.OpenConnection(location.Path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        var sql = new StringBuilder($"SELECT {Columns} FROM logs WHERE timestamp_utc >= $from AND timestamp_utc <= $to");
        command.Parameters.AddWithValue("$from", search.From.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", search.To.ToUnixTimeMilliseconds());
        In(sql, command, "level", "$level", search.Levels);
        In(sql, command, "category", "$category", search.Categories);
        if (search.User is not null)
        {
            sql.Append(" AND user_name = $user");
            command.Parameters.AddWithValue("$user", search.User);
        }

        if (search.RequestId is not null)
        {
            sql.Append(" AND request_id = $requestId");
            command.Parameters.AddWithValue("$requestId", search.RequestId);
        }

        if (search.FullTextQuery is not null)
        {
            sql.Append(" AND id IN (SELECT rowid FROM logs_fts WHERE logs_fts MATCH $q)");
            command.Parameters.AddWithValue("$q", search.FullTextQuery);
        }

        if (search.After is { } after)
        {
            sql.Append(" AND (timestamp_utc < $afterTimestamp OR (timestamp_utc = $afterTimestamp AND id < $afterId))");
            command.Parameters.AddWithValue("$afterTimestamp", after.TimestampUtcMs);
            command.Parameters.AddWithValue("$afterId", after.Id);
        }

        // One more than the page: tells whether older entries follow.
        sql.Append(" ORDER BY timestamp_utc DESC, id DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", search.Limit + 1);
        command.CommandText = sql.ToString();

        var items = new List<LogRecord>(Math.Min(search.Limit + 1, 256));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new LogRecord(
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
        }

        LogCursor? next = null;
        if (items.Count > search.Limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = new LogCursor(last.Timestamp.ToUnixTimeMilliseconds(), last.Id);
        }

        return new LogPage(items, next);
    }

    public async Task<IReadOnlyList<string>> UsersAsync(CancellationToken cancellationToken)
    {
        using var connection = SqliteLogStore.OpenConnection(location.Path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        // DISTINCT walks ix_logs_user_timestamp, not the table.
        command.CommandText = "SELECT DISTINCT user_name FROM logs WHERE user_name IS NOT NULL ORDER BY user_name";
        var users = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(reader.GetString(0));
        }

        users.Sort(StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase));
        return users;
    }

    private static void In(StringBuilder sql, SqliteCommand command, string column, string prefix, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        sql.Append(" AND ").Append(column).Append(" IN (");
        for (var i = 0; i < values.Count; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            sql.Append(i == 0 ? string.Empty : ", ").Append(name);
            command.Parameters.AddWithValue(name, values[i]);
        }

        sql.Append(')');
    }
}
