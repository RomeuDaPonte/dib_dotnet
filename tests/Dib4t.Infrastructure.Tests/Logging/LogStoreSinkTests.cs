using System.Diagnostics;
using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common.Logging;
using Serilog.Events;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>Batching and the drop-not-block policy of the log store sink (PLAN.md §3.3, §11.9).</summary>
public sealed class LogStoreSinkTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static LogsOptions Options(int queueLimit = 10_000, int batchSize = 1_000, int periodMs = 30_000) => new()
    {
        QueueLimit = queueLimit,
        BatchSizeLimit = batchSize,
        BatchPeriod = TimeSpan.FromMilliseconds(periodMs),
    };

    [Fact]
    public async Task Events_are_written_in_batches_of_at_most_the_limit_in_emit_order()
    {
        var writer = new RecordingBatchWriter();
        await using var sink = new LogStoreSink(writer, Options(), TimeProvider.System);

        for (var i = 0; i < 2_500; i++)
        {
            sink.Emit(LogEvents.Create(i));
        }

        await sink.FlushAsync(Cancellation);

        writer.Batches.Select(batch => batch.Count).Should().Equal(1_000, 1_000, 500);
        writer.Entries.Select(entry => entry.Message).Should().Equal(Enumerable.Range(0, 2_500).Select(i => $"Event {i}"));
    }

    [Fact]
    public async Task A_batch_is_written_after_the_batch_period_without_a_flush()
    {
        var path = LogStoreFile.NewTempPath();
        try
        {
            await using (var sink = new LogStoreSink(new SqliteLogStore(path), Options(periodMs: 100), TimeProvider.System))
            {
                var stopwatch = Stopwatch.StartNew();
                sink.Emit(LogEvents.Create(1));

                var rows = await LogStoreFile.WaitForAsync(path, rows => rows.Count == 1, cancellationToken: Cancellation);

                rows.Should().ContainSingle().Which.Message.Should().Be("Event 1");
                stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));
            }
        }
        finally
        {
            LogStoreFile.Delete(path);
        }
    }

    [Fact]
    public async Task A_full_queue_drops_new_events_without_blocking_and_the_drop_count_is_logged()
    {
        var writer = new RecordingBatchWriter(blocking: true);
        await using var sink = new LogStoreSink(writer, Options(queueLimit: 10, batchSize: 1, periodMs: 1), TimeProvider.System);
        sink.Emit(LogEvents.Create(0));
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation); // the writer is stuck on event 0

        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= 15; i++)
        {
            sink.Emit(LogEvents.Create(i));
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "logging never waits for the store");
        sink.DroppedTotal.Should().Be(5);

        writer.Release();
        await sink.FlushAsync(Cancellation);

        writer.Entries.Where(entry => entry.Message.StartsWith("Event", StringComparison.Ordinal)).Select(entry => entry.Message)
            .Should().Equal(Enumerable.Range(0, 11).Select(i => $"Event {i}"));
        writer.Entries.Should().ContainSingle(entry => entry.Message == "5 log entries dropped")
            .Which.Should().Match<LogEntry>(entry => entry.Level == "Warning" && entry.Category == "App" && entry.PropertiesJson == """{"Dropped":5}""");
    }

    [Fact]
    public async Task A_failing_write_does_not_reach_the_caller_and_is_reported_as_dropped()
    {
        var writer = new RecordingBatchWriter(failures: 1);
        await using var sink = new LogStoreSink(writer, Options(), TimeProvider.System);
        for (var i = 0; i < 3; i++)
        {
            sink.Emit(LogEvents.Create(i));
        }

        await sink.FlushAsync(Cancellation); // first write fails
        sink.Emit(LogEvents.Create(3));
        await sink.FlushAsync(Cancellation);

        sink.DroppedTotal.Should().Be(3);
        writer.Entries.Select(entry => entry.Message).Should().Equal("Event 3", "3 log entries dropped");
    }

    [Fact]
    public async Task Dispose_writes_the_queued_events_and_closes_the_store()
    {
        var writer = new RecordingBatchWriter();
        var sink = new LogStoreSink(writer, Options(), TimeProvider.System);
        for (var i = 0; i < 5; i++)
        {
            sink.Emit(LogEvents.Create(i, LogEventLevel.Warning));
        }

        await sink.DisposeAsync();
#pragma warning disable CA1849 // the synchronous path is the one under test
        sink.Dispose(); // idempotent (Serilog and the container both dispose it)
#pragma warning restore CA1849

        writer.Entries.Should().HaveCount(5);
        writer.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Events_after_dispose_are_counted_as_dropped()
    {
        var writer = new RecordingBatchWriter();
        var sink = new LogStoreSink(writer, Options(), TimeProvider.System);
        await sink.DisposeAsync();

        sink.Emit(LogEvents.Create(1));

        sink.DroppedTotal.Should().Be(1);
    }
}
