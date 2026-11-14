using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Dib4t.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// Logs every MongoDB command the app sends, in one central place (PLAN.md §3.3, category <c>Database</c>): command,
/// database, collection, duration and result size (<c>n</c>, <c>nModified</c>, returned documents), or the error
/// code and message. <b>Command bodies and replies are never logged</b> (password hashes, business data): only the
/// collection name is read from the command and only counts from the reply. Driver housekeeping (handshake, auth,
/// heartbeats, sessions) is skipped. Started/finished events are matched by the driver's request id.
/// </summary>
public sealed partial class MongoCommandLogger
{
    private static readonly FrozenSet<string> SkippedCommands = new[]
    {
        "hello", "isMaster", "ismaster", "ping", "saslStart", "saslContinue", "getnonce", "authenticate", "endSessions",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILogger<MongoCommandLogger> _logger;
    private readonly TimeSpan _slowCommand;
    private readonly ConcurrentDictionary<int, StartedCommand> _inFlight = new();

    public MongoCommandLogger(ILogger<MongoCommandLogger> logger, IOptions<LogsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _slowCommand = TimeSpan.FromMilliseconds(options.Value.SlowCommandMs);
    }

    /// <summary>For <see cref="MongoClientSettings.ClusterConfigurator"/>.</summary>
    public void Subscribe(ClusterBuilder cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        cluster.Subscribe<CommandStartedEvent>(OnStarted);
        cluster.Subscribe<CommandSucceededEvent>(OnSucceeded);
        cluster.Subscribe<CommandFailedEvent>(OnFailed);
    }

    internal static string? CollectionOf(string commandName, BsonDocument command)
    {
        if (command.ElementCount == 0)
        {
            return null;
        }

        // getMore names the cursor id first and the collection in "collection"; the others name it first.
        var value = commandName == "getMore"
            ? command.GetValue("collection", BsonNull.Value)
            : command.GetElement(0).Value;
        return value.IsString ? value.AsString : null;
    }

    /// <summary><c>n=…</c>, <c>nModified=…</c>, <c>returned=…</c>, only those the reply has.</summary>
    internal static string ResultOf(string commandName, BsonDocument reply)
    {
        var result = new StringBuilder();
        if (commandName == "findAndModify")
        {
            Append(result, "n", reply.GetValue("lastErrorObject", BsonNull.Value) is BsonDocument lastError
                ? Number(lastError, "n")
                : null);
        }
        else
        {
            Append(result, "n", Number(reply, "n"));
            Append(result, "nModified", Number(reply, "nModified"));
        }

        if (reply.GetValue("cursor", BsonNull.Value) is BsonDocument cursor)
        {
            var batch = cursor.GetValue("firstBatch", BsonNull.Value);
            if (batch.IsBsonNull)
            {
                batch = cursor.GetValue("nextBatch", BsonNull.Value);
            }

            Append(result, "returned", batch.IsBsonArray ? batch.AsBsonArray.Count : null);
        }

        return result.ToString();
    }

    private static long? Number(BsonDocument document, string name) =>
        document.GetValue(name, BsonNull.Value) is { IsNumeric: true } value ? value.ToInt64() : null;

    private static void Append(StringBuilder result, string name, long? value)
    {
        if (value is null)
        {
            return;
        }

        if (result.Length > 0)
        {
            result.Append(' ');
        }

        result.Append(name).Append('=').Append(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static double Milliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 1);

    private void OnStarted(CommandStartedEvent e)
    {
        if (SkippedCommands.Contains(e.CommandName))
        {
            return;
        }

        _inFlight[e.RequestId] = new StartedCommand(
            e.DatabaseNamespace?.DatabaseName ?? string.Empty,
            CollectionOf(e.CommandName, e.Command));
    }

    private void OnSucceeded(CommandSucceededEvent e)
    {
        if (!_inFlight.TryRemove(e.RequestId, out var started))
        {
            return;
        }

        var reply = e.Reply;
        if (reply.GetValue("writeErrors", BsonNull.Value) is BsonArray { Count: > 0 } writeErrors)
        {
            var code = writeErrors[0] is BsonDocument first ? Number(first, "code") : null;
            LogWriteErrors(e.CommandName, started.Database, started.Collection, Milliseconds(e.Duration), writeErrors.Count, code);
            return;
        }

        var slow = e.Duration > _slowCommand;
        if (!_logger.IsEnabled(slow ? LogLevel.Warning : LogLevel.Information))
        {
            return;
        }

        var duration = Milliseconds(e.Duration);
        var result = ResultOf(e.CommandName, reply);
        if (slow)
        {
            LogSlow(e.CommandName, started.Database, started.Collection, duration, result);
        }
        else
        {
            LogSucceeded(e.CommandName, started.Database, started.Collection, duration, result);
        }
    }

    private void OnFailed(CommandFailedEvent e)
    {
        if (!_inFlight.TryRemove(e.RequestId, out var started))
        {
            return;
        }

        var (code, codeName) = e.Failure is MongoCommandException command ? (command.Code, command.CodeName) : ((int?)null, (string?)null);
        // The server's error text only (e.g. "E11000 duplicate key …"); never the command or the reply document.
        var message = e.Failure is MongoCommandException commandFailure ? commandFailure.ErrorMessage : e.Failure?.Message;
        LogFailed(e.CommandName, started.Database, started.Collection, Milliseconds(e.Duration), code, codeName, message, e.Failure?.GetType().Name);
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Mongo {CommandName} {Database}.{Collection} in {DurationMs} ms {Result}")]
    private partial void LogSucceeded(string commandName, string database, string? collection, double durationMs, string result);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "Slow Mongo {CommandName} {Database}.{Collection} in {DurationMs} ms {Result}")]
    private partial void LogSlow(string commandName, string database, string? collection, double durationMs, string result);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error,
        Message = "Mongo {CommandName} {Database}.{Collection} failed in {DurationMs} ms: {ErrorCode} {ErrorCodeName} {ErrorMessage} ({ErrorType})")]
    private partial void LogFailed(string commandName, string database, string? collection, double durationMs, int? errorCode, string? errorCodeName, string? errorMessage, string? errorType);

    [LoggerMessage(EventId = 103, Level = LogLevel.Error,
        Message = "Mongo {CommandName} {Database}.{Collection} in {DurationMs} ms: {WriteErrors} write errors (first code {ErrorCode})")]
    private partial void LogWriteErrors(string commandName, string database, string? collection, double durationMs, int writeErrors, long? errorCode);

    private readonly record struct StartedCommand(string Database, string? Collection);
}
