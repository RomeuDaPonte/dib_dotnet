using Dib4t.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Dib4t.Infrastructure.Tests.Logging;

internal static class LogEvents
{
    private static readonly MessageTemplateParser Parser = new();

    public static LogEvent Create(int n, LogEventLevel level = LogEventLevel.Information) => new(
        DateTimeOffset.UtcNow, level, null, Parser.Parse("Event {N}"),
        [new LogEventProperty("N", new ScalarValue(n))]);
}

/// <summary>Records each batch; can hold the writer inside <see cref="Write"/> to fill the queue.</summary>
internal sealed class RecordingBatchWriter : ILogBatchWriter
{
    private readonly SemaphoreSlim? _gate;
    private int _failuresLeft;

    public RecordingBatchWriter(bool blocking = false, int failures = 0)
    {
        _gate = blocking ? new SemaphoreSlim(0) : null;
        _failuresLeft = failures;
    }

    public List<IReadOnlyList<LogEntry>> Batches { get; } = [];

    public IEnumerable<LogEntry> Entries => Batches.SelectMany(batch => batch);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public void Release() => _gate?.Release(int.MaxValue / 2);

    public void Write(IReadOnlyList<LogEntry> entries)
    {
        Entered.TrySetResult();
        _gate?.Wait();
        if (_failuresLeft > 0)
        {
            _failuresLeft--;
            throw new IOException("disk stalled");
        }

        lock (Batches)
        {
            Batches.Add([.. entries]);
        }
    }

    public void Dispose()
    {
        Disposed = true;
        _gate?.Dispose();
    }
}

internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}

/// <summary>Microsoft.Extensions.Logging provider that keeps every entry with its structured properties.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<RecordedLog> _entries = [];

    public IReadOnlyList<RecordedLog> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            lock (provider._entries)
            {
                provider._entries.Add(new RecordedLog(category, logLevel, eventId.Id, formatter(state, exception), properties));
            }
        }
    }
}

internal sealed record RecordedLog(string Category, LogLevel Level, int EventId, string Message, IReadOnlyDictionary<string, object?> Properties)
{
    public object? this[string property] => Properties.GetValueOrDefault(property);
}
