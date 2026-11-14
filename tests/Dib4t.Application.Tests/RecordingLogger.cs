using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Tests;

/// <summary>Keeps every log entry (all levels enabled), so tests can check the use-case events (PLAN.md §3.3).</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, int EventId, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, eventId.Id, formatter(state, exception), exception));
    }
}
