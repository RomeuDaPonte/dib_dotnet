using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static Dib4t.Infrastructure.Tests.Logging.LogStoreSeed;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>
/// The daily log cleanup (PLAN.md §3.3) on a real store file with a fake clock: retention by calendar month, chunked
/// deletes, size guard, space given back, the <c>meta</c> record, catch-up at startup, the 02:00 schedule, failures.
/// </summary>
public sealed class LogCleanupServiceTests : IDisposable
{
    // 2027-03-31 02:00 in Lisbon (WEST, UTC+1): the cutoff is 2027-02-28 02:00 WET = 02:00 UTC.
    private static readonly DateTimeOffset RunTime = new(2027, 3, 31, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2027, 2, 28, 2, 0, 0, TimeSpan.Zero);

    private readonly string _path = LogStoreFile.NewTempPath();
    private FakeTimeProvider _time = new(RunTime);
    private readonly RecordingLoggerProvider _logs = new();
    private readonly LoggerFactory _loggerFactory;

    public LogCleanupServiceTests() => _loggerFactory = new LoggerFactory([_logs]);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _loggerFactory.Dispose();
        LogStoreFile.Delete(_path);
    }

    private LogCleanupService Service(LogsOptions? options = null, string? path = null) => new(
        new LogStoreLocation(path ?? _path), Options.Create(options ?? new LogsOptions { Cleanup = { ChunkSize = 2 } }), _time,
        _loggerFactory.CreateLogger<LogCleanupService>());

    private Task<object?> ScalarAsync(string sql) => LogStoreFile.ScalarAsync(_path, sql, Cancellation);

    [Fact]
    public async Task Deletes_entries_older_than_one_calendar_month_in_chunks_and_records_the_run()
    {
        Write(_path,
            Entry(Cutoff.AddDays(-20), "very old"),
            Entry(Cutoff.AddMinutes(-1), "old 1"),
            Entry(Cutoff.AddMilliseconds(-1), "old 2 searchable"),
            Entry(Cutoff, "kept at the cutoff"),
            Entry(RunTime.AddHours(-1), "recent"));
        using var service = Service();

        (await service.RunOnceAsync(Cancellation)).Should().BeTrue();

        (await LogStoreFile.ReadAsync(_path, Cancellation)).Select(r => r.Message).Should().Equal("kept at the cutoff", "recent");
        (await ScalarAsync("SELECT count(*) FROM logs_fts WHERE logs_fts MATCH 'searchable';")).Should().Be(0L);
        (await ScalarAsync("SELECT last_cleanup_utc FROM meta WHERE id = 1;")).Should().Be(RunTime.ToUnixTimeMilliseconds());
        var finished = _logs.Entries.Should().ContainSingle(e => e.EventId == 202).Subject;
        finished.Level.Should().Be(LogLevel.Information);
        finished["Deleted"].Should().Be(3L);
        finished["Cutoff"].Should().Be(Cutoff);
        _logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        LogCategoryMap.Resolve(finished.Category).Should().Be("Jobs");
    }

    [Fact]
    public async Task The_size_guard_deletes_the_oldest_entries_and_the_space_is_given_back()
    {
        var payload = new string('x', 4000);
        Write(_path, [.. Enumerable.Range(0, 1200).Select(i => Entry(RunTime.AddDays(-10).AddSeconds(i), $"entry {i} {payload}"))]);
        var before = new FileInfo(_path).Length;
        before.Should().BeGreaterThan(4 * 1024 * 1024);
        using var service = Service(new LogsOptions { MaxSizeMB = 1, Cleanup = { ChunkSize = 100 } });

        (await service.RunOnceAsync(Cancellation)).Should().BeTrue();

        var rows = await LogStoreFile.ReadAsync(_path, Cancellation);
        rows.Should().NotBeEmpty().And.HaveCountLessThan(300);
        rows[^1].Message.Should().StartWith("entry 1199 ", "the newest entries are kept");
        new FileInfo(_path).Length.Should().BeLessThan(2 * 1024 * 1024, "incremental vacuum gives the pages back");
        var guard = _logs.Entries.Should().ContainSingle(e => e.EventId == 203).Subject;
        guard.Level.Should().Be(LogLevel.Warning);
        guard["Deleted"].Should().Be((long)(1200 - rows.Count));
    }

    [Fact]
    public async Task At_startup_it_catches_up_when_there_was_no_cleanup_in_the_last_24_hours_then_waits_for_02_00()
    {
        Write(_path, Entry(Cutoff.AddDays(-1), "old"));
        _time = new FakeTimeProvider(new DateTimeOffset(2027, 3, 30, 12, 0, 0, TimeSpan.Zero)); // no cleanup recorded yet
        using var service = Service();

        await service.StartAsync(Cancellation);
        await WaitForRunsAsync(service, 1);
        (await ScalarAsync("SELECT count(*) FROM logs;")).Should().Be(0L);
        _logs.Entries.Should().Contain(e => e.EventId == 200);

        Write(_path, Entry(new DateTimeOffset(2027, 2, 27, 0, 0, 0, TimeSpan.Zero), "old again"));
        await AdvanceUntilAsync(service, 2, new DateTimeOffset(2027, 3, 31, 0, 59, 0, TimeSpan.Zero));
        _time.GetUtcNow().Should().BeOnOrAfter(RunTime).And.BeBefore(RunTime.AddMinutes(1));
        (await ScalarAsync("SELECT count(*) FROM logs;")).Should().Be(0L);
        await service.StopAsync(Cancellation);
    }

    [Fact]
    public async Task No_catch_up_after_a_recent_cleanup()
    {
        using (var seeded = new LogStoreMaintenance(CreateStore()))
        {
            seeded.SetLastCleanup(RunTime.AddHours(-23));
        }

        using var service = Service();
        await service.StartAsync(Cancellation);
        await Task.Delay(200, Cancellation);

        service.Runs.Should().Be(0);
        _logs.Entries.Should().NotContain(e => e.EventId == 200);
        await service.StopAsync(Cancellation);
    }

    [Fact]
    public async Task A_failure_is_an_Error_and_the_job_keeps_its_schedule()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"dib4t-missing-{Guid.NewGuid():N}", "logs.db");
        using var service = Service(path: missing);

        (await service.RunOnceAsync(Cancellation)).Should().BeFalse();

        var failed = _logs.Entries.Should().ContainSingle(e => e.EventId == 204).Subject;
        failed.Level.Should().Be(LogLevel.Error);
    }

    private string CreateStore()
    {
        new SqliteLogStore(_path).Dispose();
        return _path;
    }

    private static async Task WaitForRunsAsync(LogCleanupService service, int runs)
    {
        for (var i = 0; i < 500 && service.Runs < runs; i++)
        {
            await Task.Delay(10, Cancellation);
        }

        service.Runs.Should().Be(runs);
    }

    /// <summary>Moves the clock to <paramref name="start"/>, then one second at a time until the job has run.</summary>
    private async Task AdvanceUntilAsync(LogCleanupService service, int runs, DateTimeOffset start)
    {
        await Task.Delay(50, Cancellation); // the service is waiting for its next run
        _time.SetUtcNow(start);
        for (var i = 0; i < 600 && service.Runs < runs; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, Cancellation);
        }

        await WaitForRunsAsync(service, runs);
    }
}
