using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Dib4t.Application.Tests.Database;

/// <summary>Legacy <c>GET /api/database/info</c> and <c>GET /api/database/backup</c> flows (<c>routes/database.js</c>): busy lock, failures, log events.</summary>
public sealed class DatabaseBackupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T10:15:30.123Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DatabaseInfo Info = new("dib4t", "localhost", 27018, "9.0.8", 1, 3, 40, 120, 4096, 1, 4096,
        [new CollectionInfo("jobs", 3, 120, 40, 4096, 1, 4096)]);

    private readonly IDatabaseBackupService _backups = Substitute.For<IDatabaseBackupService>();
    private readonly DatabaseOperationLock _lock = new();
    private readonly RecordingLogger<DatabaseBackupService> _logger = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly DatabaseBackupService _service;
    private readonly MemoryStream _output = new();
    private int _opened;

    public DatabaseBackupServiceTests()
    {
        _service = new DatabaseBackupService(_backups, _lock, _time, _logger);
        _backups.GetInfoAsync(Arg.Any<CancellationToken>()).Returns(Info);
        _backups.WriteBackupAsync(Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new BackupSummary(8, 30_754));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public void Dispose() => _output.Dispose();

    private Stream Open()
    {
        _opened++;
        return _output;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DatabaseOperationLock.Backup)]
    [InlineData(DatabaseOperationLock.Restore)]
    public async Task Info_adds_when_it_was_read_and_the_running_operation(string? running)
    {
        using var held = running is null ? null : _lock.TryAcquire(running);

        var snapshot = await _service.GetInfoAsync(Cancellation);

        snapshot.Should().Be(new DatabaseInfoSnapshot(Info, Now.UtcDateTime, running));
        snapshot.FetchedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Info_does_not_take_the_lock()
    {
        _backups.GetInfoAsync(Arg.Any<CancellationToken>()).Returns(_ => { _lock.BusyWith.Should().BeNull(); return Info; });

        await _service.GetInfoAsync(Cancellation);

        _lock.BusyWith.Should().BeNull();
    }

    [Fact]
    public async Task Backup_holds_the_lock_while_it_writes_to_the_opened_output_and_logs_its_counts()
    {
        string? busyWhileWriting = null;
        _backups.WriteBackupAsync(_output, "ana.admin@dib4t.local", Now.UtcDateTime, Arg.Any<CancellationToken>())
            .Returns(_ => { busyWhileWriting = _lock.BusyWith; return new BackupSummary(8, 30_754); });

        var result = await _service.BackupAsync(Open, "ana.admin@dib4t.local", Cancellation);

        result.Value.Should().Be(new BackupSummary(8, 30_754));
        busyWhileWriting.Should().Be("backup");
        _lock.BusyWith.Should().BeNull("released afterwards");
        _opened.Should().Be(1);
        _logger.Entries.Select(e => (e.Level, e.EventId, e.Message)).Should().Equal(
            (LogLevel.Information, 10, "Database backup started (by ana.admin@dib4t.local)"),
            (LogLevel.Information, 11, "Database backup completed: 8 collections, 30754 documents"));
    }

    [Theory]
    [InlineData(DatabaseOperationLock.Backup, "Já existe uma operação em curso (backup). Tente mais tarde.")]
    [InlineData(DatabaseOperationLock.Restore, "Já existe uma operação em curso (restauro). Tente mais tarde.")]
    public async Task Backup_while_another_operation_runs_is_a_conflict_before_anything_is_sent(string running, string message)
    {
        using var held = _lock.TryAcquire(running);

        var result = await _service.BackupAsync(Open, null, Cancellation);

        result.Error.Should().Be(Error.Conflict(message));
        _opened.Should().Be(0, "legacy checks the lock before it sets the download headers");
        await _backups.DidNotReceiveWithAnyArgs().WriteBackupAsync(default!, default, default, Cancellation);
        _lock.BusyWith.Should().Be(running, "the other operation still holds it");
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_is_Erro_ao_criar_o_backup_logged_with_the_exception_and_frees_the_lock()
    {
        var failure = new IOException("disk full");
        _backups.WriteBackupAsync(Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var result = await _service.BackupAsync(Open, null, Cancellation);

        (result.Error!.Kind, result.Error.Message, result.Error.Reason).Should().Be((ErrorKind.Failure, "Erro ao criar o backup.", "disk full"));
        _lock.BusyWith.Should().BeNull();
        _logger.Entries.Last().Should().Be((LogLevel.Error, 13, "Database backup failed", (Exception?)failure));
        using var next = _lock.TryAcquire(DatabaseOperationLock.Restore);
        next.Should().NotBeNull("the next operation can run");
    }

    [Fact]
    public async Task A_client_that_goes_away_is_logged_as_cancelled_not_as_an_error()
    {
        using var aborted = new CancellationTokenSource();
        _backups.WriteBackupAsync(Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<Task<BackupSummary>>(async _ =>
            {
                await aborted.CancelAsync();
                throw new IOException("connection reset");
            });

        var result = await _service.BackupAsync(Open, null, aborted.Token);

        result.Error!.Kind.Should().Be(ErrorKind.Failure);
        _lock.BusyWith.Should().BeNull();
        _logger.Entries.Last().Should().Be((LogLevel.Warning, 12, "Database backup cancelled: the client went away", (Exception?)null));
        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task The_header_time_is_the_clock_now_in_UTC()
    {
        _time.Advance(TimeSpan.FromHours(2));

        await _service.BackupAsync(Open, null, Cancellation);

        await _backups.Received(1).WriteBackupAsync(_output, null,
            Arg.Is<DateTime>(d => d == Now.UtcDateTime.AddHours(2) && d.Kind == DateTimeKind.Utc), Arg.Any<CancellationToken>());
    }
}
