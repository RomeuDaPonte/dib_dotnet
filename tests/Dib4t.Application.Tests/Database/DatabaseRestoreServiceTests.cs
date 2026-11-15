using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Dib4t.Application.Tests.Database;

/// <summary>Legacy <c>POST /api/database/restore</c> flow (<c>routes/database.js</c>): empty file, busy lock, errors.</summary>
public sealed class DatabaseRestoreServiceTests
{
    private static readonly RestoreSummary Summary =
        new(JsonElement.Parse("\"2026-11-11T16:15:31.123Z\""), JsonElement.Parse("\"dib4t\""), 8, 30_754);

    private readonly IDatabaseBackupService _backups = Substitute.For<IDatabaseBackupService>();
    private readonly DatabaseOperationLock _lock = new();
    private readonly RecordingLogger<DatabaseRestoreService> _logger = new();
    private readonly UserSessionCache _sessions = new(Options.Create(new UserSessionOptions()), TimeProvider.System);
    private readonly DatabaseRestoreService _service;

    public DatabaseRestoreServiceTests() =>
        _service = new DatabaseRestoreService(_backups, _lock, _sessions, _logger);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_forgets_the_cached_users_so_tokens_are_checked_against_the_restored_ones()
    {
        var loads = 0;
        Task<User?> Load(CancellationToken _) { loads++; return Task.FromResult<User?>(null); }
        await _sessions.GetOrLoadAsync("5f1e9a2b3c4d5e6f70819203", Load, Cancellation);
        _backups.RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(Result<RestoreSummary>.Success(Summary));

        await _service.RestoreAsync(new MemoryStream([1]), Cancellation);
        await _sessions.GetOrLoadAsync("5f1e9a2b3c4d5e6f70819203", Load, Cancellation);

        loads.Should().Be(2);
    }

    [Fact]
    public async Task Empty_file_is_rejected_without_taking_the_lock()
    {
        using var busy = _lock.TryAcquire(DatabaseOperationLock.Backup);

        var result = await _service.RestoreAsync(Stream.Null, Cancellation);

        // Legacy checks the body before the lock: "no file" wins over 409.
        result.Error.Should().Be(Error.Validation("Nenhum ficheiro de backup recebido."));
        await _backups.DidNotReceive().RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DatabaseOperationLock.Backup, "Já existe uma operação em curso (backup). Tente mais tarde.")]
    [InlineData(DatabaseOperationLock.Restore, "Já existe uma operação em curso (restauro). Tente mais tarde.")]
    public async Task Restore_while_another_operation_runs_is_a_conflict(string running, string message)
    {
        using var busy = _lock.TryAcquire(running);

        var result = await _service.RestoreAsync(OneByte(), Cancellation);

        result.Error.Should().Be(Error.Conflict(message));
        await _backups.DidNotReceive().RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_holds_the_lock_while_it_runs_and_releases_it_after()
    {
        string? busyDuringRestore = null;
        _backups.RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            busyDuringRestore = _lock.BusyWith;
            return Result<RestoreSummary>.Success(Summary);
        });

        var result = await _service.RestoreAsync(OneByte(), Cancellation);

        result.Value.Should().Be(Summary);
        busyDuringRestore.Should().Be("restauro");
        _lock.BusyWith.Should().BeNull();
        _logger.Entries.Select(e => (e.Level, e.Message)).Should().Equal(
            (LogLevel.Information, "Database restore started (1 bytes)"),
            (LogLevel.Information, "Database restored: 8 collections, 30754 documents"));
    }

    [Fact]
    public async Task Rejected_file_returns_the_adapter_message_and_releases_the_lock()
    {
        _backups.RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Result<RestoreSummary>.Failure(Error.Validation("O ficheiro não é um backup válido da Dib4t.")));

        var result = await _service.RestoreAsync(OneByte(), Cancellation);

        result.Error.Should().Be(Error.Validation("O ficheiro não é um backup válido da Dib4t."));
        _lock.BusyWith.Should().BeNull();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Be("Database restore rejected: O ficheiro não é um backup válido da Dib4t.");
    }

    [Fact]
    public async Task Unexpected_failure_is_the_legacy_500_message_and_releases_the_lock()
    {
        _backups.RestoreAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.RestoreAsync(OneByte(), Cancellation);

        result.Error.Should().Be(Error.Failure("Erro ao restaurar o backup."));
        _lock.BusyWith.Should().BeNull();
        // The exception (with its stack trace) goes to the log, never to the response (§3.2).
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Cancelled_request_is_not_reported_as_a_failed_restore()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _backups.RestoreAsync(Arg.Any<Stream>(), cancelled.Token).ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var act = () => _service.RestoreAsync(OneByte(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _lock.BusyWith.Should().BeNull();
    }

    private static MemoryStream OneByte() => new([0x1F]);
}
