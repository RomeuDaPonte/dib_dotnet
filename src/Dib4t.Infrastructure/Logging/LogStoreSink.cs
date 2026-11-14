using System.Threading.Channels;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// Serilog sink in front of the log store (PLAN.md §3.3). Logging never waits for disk: <see cref="Emit"/> only puts
/// the event in a bounded queue. One background writer takes up to <see cref="LogsOptions.BatchSizeLimit"/> events,
/// or what arrived within <see cref="LogsOptions.BatchPeriod"/> of the first one, and writes them in one transaction.
/// When the queue is full the event is <b>dropped, not blocked</b>; the count is written as a Warning
/// "N log entries dropped" with the next batch. Serilog's own batching sink is not used because it drops silently
/// (self-log only), so the drop count could not reach the store.
/// </summary>
public sealed class LogStoreSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogBatchWriter _writer;
    private readonly TimeProvider _time;
    private readonly int _batchSizeLimit;
    private readonly TimeSpan _batchPeriod;
    private readonly Channel<QueueItem> _queue;
    private readonly Task _writeLoop;
    private long _pendingDropped;
    private long _droppedTotal;
    private int _disposed;

    internal LogStoreSink(ILogBatchWriter writer, LogsOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _writer = writer;
        _time = time;
        _batchSizeLimit = options.BatchSizeLimit;
        _batchPeriod = options.BatchPeriod;
        _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(options.QueueLimit)
        {
            // Wait mode makes TryWrite return false when full (the Drop* modes report success for a dropped item).
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Events dropped because the queue was full (or a write failed) since the sink started.</summary>
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (!_queue.Writer.TryWrite(new QueueItem(logEvent, null)))
        {
            Interlocked.Increment(ref _pendingDropped);
            Interlocked.Increment(ref _droppedTotal);
        }
    }

    /// <summary>Completes when every event emitted before the call is written (tests, shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new QueueItem(null, flushed), cancellationToken).ConfigureAwait(false);
        await flushed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes what is queued (up to 10 s), then closes the store.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        if (!_writeLoop.Wait(DisposeTimeout))
        {
            SelfLog.WriteLine("Log store: queued entries not written within {0} at shutdown", DisposeTimeout);
        }

        _writer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            await _writeLoop.WaitAsync(DisposeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SelfLog.WriteLine("Log store: queued entries not written within {0} at shutdown", DisposeTimeout);
        }

        _writer.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<LogEvent>(Math.Min(_batchSizeLimit, 1024));
        TaskCompletionSource? flush = null;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var start = _time.GetTimestamp();
            while (true)
            {
                while (flush is null && batch.Count < _batchSizeLimit && reader.TryRead(out var item))
                {
                    if (item.Flush is not null)
                    {
                        flush = item.Flush;
                    }
                    else
                    {
                        batch.Add(item.Event!);
                    }
                }

                var remaining = _batchPeriod - _time.GetElapsedTime(start);
                if (flush is not null || batch.Count >= _batchSizeLimit || remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var window = new CancellationTokenSource(remaining, _time);
                try
                {
                    if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        break; // completed (shutdown): write what we have
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // batch period over
                }
            }

            WriteBatch(batch);
            batch.Clear();
            flush?.TrySetResult();
            flush = null;
        }

        WriteBatch(batch); // drop count of events emitted after the last batch
    }

    private void WriteBatch(List<LogEvent> batch)
    {
        var dropped = Interlocked.Exchange(ref _pendingDropped, 0);
        if (batch.Count == 0 && dropped == 0)
        {
            return;
        }

        var entries = new List<LogEntry>(batch.Count + 1);
        foreach (var logEvent in batch)
        {
            try
            {
                entries.Add(LogEventMapper.ToEntry(logEvent));
            }
#pragma warning disable CA1031 // one bad event (e.g. a throwing ToString) must not lose the batch
            catch (Exception ex)
#pragma warning restore CA1031
            {
                SelfLog.WriteLine("Log store: event skipped: {0}", ex);
            }
        }

        if (dropped > 0)
        {
            entries.Add(LogEventMapper.Dropped(dropped, _time.GetUtcNow()));
        }

        try
        {
            _writer.Write(entries);
        }
#pragma warning disable CA1031 // the writer must survive a failing disk; the entries count as dropped
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SelfLog.WriteLine("Log store: batch of {0} entries not written: {1}", entries.Count, ex);
            Interlocked.Add(ref _pendingDropped, batch.Count + dropped);
            Interlocked.Add(ref _droppedTotal, batch.Count);
        }
    }

    private readonly record struct QueueItem(LogEvent? Event, TaskCompletionSource? Flush);
}
