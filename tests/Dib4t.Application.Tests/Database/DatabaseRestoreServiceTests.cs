using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Dib4t.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Dib4t.Application.Tests.Database;

/// <summary>
/// <c>POST /api/database/restore</c> (legacy <c>routes/database.js</c>, hardened in PLAN.md §7.6): empty file, busy
/// lock, validation → pre-restore backup → replace → verification, the progress that can be read again, a client
/// that stops waiting, failures and their log events.
/// </summary>
public sealed class DatabaseRestoreServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T10:15:30.123Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly RestoreSummary Summary =
        new(JsonElement.Parse("\"2026-11-11T16:15:31.123Z\""), JsonElement.Parse("\"dib4t\""), 2, 30_754);

    private static readonly RestoreAdjustment Cleaned = new("users", "email_1", ["background"]);
    private static readonly RestoreAdjustment Refused = new("jobs", "pos_2d", [], IndexRemoved: true, ServerMessage: "bad index");

    private static readonly BackupContents Contents = new(Summary,
        [new BackupCollectionContents("users", 4, 1, []), new BackupCollectionContents("jobs", 30_750, 2, [new ExpectedIndex("pos_2d", "{}")])],
        [Cleaned]);

    private static readonly RestoreVerification Clean = new(
        [new CollectionVerification("users", true, 4, 4, true, []), new CollectionVerification("jobs", true, 30_750, 30_750, true, [])], []);

    private readonly IDatabaseBackupService _backups = Substitute.For<IDatabaseBackupService>();
    private readonly IPreRestoreBackupStore _store = Substitute.For<IPreRestoreBackupStore>();
    private readonly DatabaseOperationLock _lock = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly RestoreTracker _tracker;
    private readonly RecordingLogger<DatabaseRestoreService> _logger = new();
    private readonly UserSessionCache _sessions = new(Options.Create(new UserSessionOptions()), TimeProvider.System);
    private readonly ServiceProvider _provider;
    private readonly DatabaseRestoreService _service;
    private readonly List<string> _calls = [];

    public DatabaseRestoreServiceTests()
    {
        _tracker = new RestoreTracker(_time);
        _provider = new ServiceCollection().AddScoped(_ => _backups).AddScoped(_ => _store).BuildServiceProvider();
        _service = new DatabaseRestoreService(_provider.GetRequiredService<IServiceScopeFactory>(), _lock, _tracker, _sessions, _time, _logger);

        _backups.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(_ => Call("validate", Result<BackupContents>.Success(Contents)));
        _store.SaveAsync(Arg.Any<Func<Stream, Task>>(), Arg.Any<DateTime>()).Returns(async call =>
        {
            Call("save", 0);
            await call.Arg<Func<Stream, Task>>()(Stream.Null);
            return "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak";
        });
        _backups.WriteBackupAsync(Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => Call("backup", new BackupSummary(2, 30_754)));
        _backups.ReplaceAsync(Arg.Any<Stream>(), Arg.Any<IRestoreProgress>())
            .Returns(_ => Call("replace", (IReadOnlyList<RestoreAdjustment>)[Refused]));
        _backups.VerifyAsync(Arg.Any<BackupContents>(), Arg.Any<CancellationToken>()).Returns(_ => Call("verify", Clean));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Restore_checks_the_file_saves_the_current_database_replaces_it_and_verifies_it_in_that_order()
    {
        var backup = new TrackedStream([1, 2, 3]);

        var result = await _service.RestoreAsync(backup, "admin@dib4t.local", "restore-1", Cancellation);

        result.Value.Should().Be(Summary);
        _calls.Should().Equal("validate:restauro", "save:restauro", "backup:restauro", "replace:restauro", "verify:restauro");
        _lock.BusyWith.Should().BeNull();
        backup.Disposed.Should().BeTrue("the restore owns the uploaded file");
        await _backups.Received(1).WriteBackupAsync(Stream.Null, "admin@dib4t.local", Now.UtcDateTime, CancellationToken.None);
        await _store.Received(1).SaveAsync(Arg.Any<Func<Stream, Task>>(), Now.UtcDateTime);
        await _backups.Received(1).ReplaceAsync(backup, _tracker);
        await _backups.Received(1).VerifyAsync(Contents, CancellationToken.None);

        _tracker.Current.Should().BeEquivalentTo(new
        {
            Id = "restore-1",
            Stage = RestoreStage.Completed,
            IsRunning = false,
            StartedAt = Now.UtcDateTime,
            FinishedAt = (DateTime?)Now.UtcDateTime,
            Bytes = 3L,
            RestoredBy = "admin@dib4t.local",
            Collections = (int?)2,
            Documents = (long?)30_754,
            CollectionsDone = 2,
            DocumentsDone = 30_754L,
            CurrentCollection = (string?)null,
            PreRestoreBackup = "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak",
            Adjustments = new[] { Cleaned, Refused },
            Verification = Clean,
            Error = (string?)null,
            DatabaseChanged = false,
        });
        _logger.Entries.Select(e => (e.Level, e.EventId, e.Message)).Should().Equal(
            (LogLevel.Information, 1, "Database restore started (3 bytes, by admin@dib4t.local)"),
            (LogLevel.Warning, 7, "Restore adjusted users index email_1 for MongoDB 9.0: left out background"),
            (LogLevel.Information, 6, "Pre-restore backup saved: dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak"),
            (LogLevel.Warning, 7, "Restore adjusted jobs index pos_2d for MongoDB 9.0: index left out (the server refused it: bad index)"),
            (LogLevel.Information, 8, "Restore verified: no differences (2 collections, 30754 documents)"),
            (LogLevel.Information, 2, "Database restored: 2 collections, 30754 documents"));
    }

    [Fact]
    public async Task Each_stage_can_be_read_while_the_restore_runs()
    {
        var seen = new List<(RestoreStage Stage, int? Collections, string? Backup, string? Current, long Done)>();
        void See() => seen.Add((_tracker.Current!.Stage, _tracker.Current.Collections, _tracker.Current.PreRestoreBackup, _tracker.Current.CurrentCollection, _tracker.Current.DocumentsDone));
        _backups.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(_ => { See(); return Result<BackupContents>.Success(Contents); });
        _backups.WriteBackupAsync(Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { See(); return new BackupSummary(0, 0); });
        _backups.ReplaceAsync(Arg.Any<Stream>(), Arg.Any<IRestoreProgress>()).Returns(call =>
        {
            See();
            call.Arg<IRestoreProgress>().Replacing("users", 1, 0);
            See();
            call.Arg<IRestoreProgress>().Replacing("jobs", 2, 1004);
            See();
            return (IReadOnlyList<RestoreAdjustment>)[];
        });
        _backups.VerifyAsync(Arg.Any<BackupContents>(), Arg.Any<CancellationToken>()).Returns(_ => { See(); return Clean; });

        await _service.RestoreAsync(OneByte(), null, null, Cancellation);

        seen.Should().Equal(
            (RestoreStage.Validating, null, null, null, 0),
            (RestoreStage.PreRestoreBackup, 2, null, null, 0),
            (RestoreStage.Replacing, 2, "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", null, 0),
            (RestoreStage.Replacing, 2, "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", "users", 0),
            (RestoreStage.Replacing, 2, "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", "jobs", 1004),
            (RestoreStage.Verifying, 2, "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", null, 30_754));
        _tracker.Current!.Id.Should().MatchRegex("^[0-9a-f]{32}$", "an id of its own when the client sends none");
    }

    [Fact]
    public async Task Success_forgets_the_cached_users_so_tokens_are_checked_against_the_restored_ones()
    {
        var loads = 0;
        Task<User?> Load(CancellationToken _) { loads++; return Task.FromResult<User?>(null); }
        await _sessions.GetOrLoadAsync("5f1e9a2b3c4d5e6f70819203", Load, Cancellation);

        await _service.RestoreAsync(OneByte(), null, null, Cancellation);
        await _sessions.GetOrLoadAsync("5f1e9a2b3c4d5e6f70819203", Load, Cancellation);

        loads.Should().Be(2);
    }

    [Fact]
    public async Task Empty_file_is_rejected_without_taking_the_lock()
    {
        using var busy = _lock.TryAcquire(DatabaseOperationLock.Backup);
        var backup = new TrackedStream([]);

        var result = await _service.RestoreAsync(backup, null, null, Cancellation);

        // Legacy checks the body before the lock: "no file" wins over 409.
        result.Error.Should().Be(Error.Validation("Nenhum ficheiro de backup recebido."));
        _calls.Should().BeEmpty();
        backup.Disposed.Should().BeTrue();
        _tracker.Current.Should().BeNull("no restore started");
    }

    [Theory]
    [InlineData(DatabaseOperationLock.Backup, "Já existe uma operação em curso (backup). Tente mais tarde.")]
    [InlineData(DatabaseOperationLock.Restore, "Já existe uma operação em curso (restauro). Tente mais tarde.")]
    public async Task Restore_while_another_operation_runs_is_a_conflict(string running, string message)
    {
        using var busy = _lock.TryAcquire(running);
        var backup = new TrackedStream([1]);

        var result = await _service.RestoreAsync(backup, null, null, Cancellation);

        result.Error.Should().Be(Error.Conflict(message));
        _calls.Should().BeEmpty();
        backup.Disposed.Should().BeTrue();
        _tracker.Current.Should().BeNull("the running restore's progress is not replaced");
    }

    [Fact]
    public async Task Rejected_file_returns_the_adapter_message_and_changes_nothing()
    {
        _backups.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Result<BackupContents>.Failure(Error.Validation("O ficheiro não é um backup válido da Dib4t.")));

        var result = await _service.RestoreAsync(OneByte(), null, null, Cancellation);

        result.Error.Should().Be(Error.Validation("O ficheiro não é um backup válido da Dib4t."));
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _backups.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default!);
        _lock.BusyWith.Should().BeNull();
        _tracker.Current.Should().BeEquivalentTo(new { Stage = RestoreStage.Rejected, Error = "O ficheiro não é um backup válido da Dib4t.", DatabaseChanged = false });
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Be("Database restore rejected: O ficheiro não é um backup válido da Dib4t.");
    }

    [Fact]
    public async Task A_failed_pre_restore_backup_stops_the_restore_before_anything_is_dropped()
    {
        _store.SaveAsync(Arg.Any<Func<Stream, Task>>(), Arg.Any<DateTime>()).ThrowsAsync(new IOException("disk full"));

        var result = await _service.RestoreAsync(OneByte(), null, null, Cancellation);

        result.Error.Should().Be(Error.Failure("Erro ao restaurar o backup.") with { Reason = "disk full" });
        await _backups.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default!);
        _lock.BusyWith.Should().BeNull();
        _tracker.Current.Should().BeEquivalentTo(new { Stage = RestoreStage.Failed, Error = "Erro ao restaurar o backup.", DatabaseChanged = false, PreRestoreBackup = (string?)null });
        var error = _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        (error.EventId, error.Message, error.Exception!.Message).Should().Be(
            (4, "Database restore failed while PreRestoreBackup; the database was not changed", "disk full"));
    }

    [Fact]
    public async Task A_failure_while_replacing_says_the_database_is_incomplete_and_where_the_previous_one_is()
    {
        _backups.ReplaceAsync(Arg.Any<Stream>(), Arg.Any<IRestoreProgress>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.RestoreAsync(OneByte(), null, null, Cancellation);

        // The exception (with its stack trace) goes to the log, never to the response (§3.2).
        result.Error!.Message.Should().Be("Erro ao restaurar o backup.");
        _lock.BusyWith.Should().BeNull();
        _tracker.Current.Should().BeEquivalentTo(new { Stage = RestoreStage.Failed, DatabaseChanged = true, PreRestoreBackup = "dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak" });
        var error = _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        (error.EventId, error.Message, error.Exception!.Message).Should().Be(
            (5, "Database restore failed while Replacing; the database is incomplete, the previous one is in dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", "boom"));
    }

    [Fact]
    public async Task Differences_found_by_the_verification_are_shown_and_logged_but_the_restore_succeeded()
    {
        var verification = new RestoreVerification(
        [
            new CollectionVerification("budgets", false, 3, 0, false, ["a_1"]),
            new CollectionVerification("jobs", true, 10, 9, false, []),
            new CollectionVerification("tasks", true, 5, 5, false, ["name_1", "b_1"]),
            new CollectionVerification("users", true, 4, 4, true, []),
        ], ["extra"]);
        _backups.VerifyAsync(Arg.Any<BackupContents>(), Arg.Any<CancellationToken>()).Returns(verification);

        var result = await _service.RestoreAsync(OneByte(), null, null, Cancellation);

        result.IsSuccess.Should().BeTrue();
        verification.HasDifferences.Should().BeTrue();
        _tracker.Current.Should().BeEquivalentTo(new { Stage = RestoreStage.Completed, Verification = verification });
        var warning = _logger.Entries.Should().ContainSingle(e => e.EventId == 9).Which;
        (warning.Level, warning.Message).Should().Be((LogLevel.Warning,
            "Restore verification found differences: budgets: missing; jobs: 9 of 10 documents; tasks: documents differ from the file; tasks: missing indexes name_1, b_1; extra: not in the file"));
    }

    [Fact]
    public void Verification_without_any_difference()
    {
        Clean.HasDifferences.Should().BeFalse();
        new RestoreVerification([], ["x"]).HasDifferences.Should().BeTrue();
        new CollectionVerification("a", true, 1, 1, true, ["i"]).Matches.Should().BeFalse();
        new CollectionVerification("a", true, 1, 1, false, []).Matches.Should().BeFalse();
        new CollectionVerification("a", false, 0, 0, true, []).Matches.Should().BeFalse();
    }

    [Fact]
    public async Task A_client_that_stops_waiting_does_not_stop_the_restore()
    {
        var replacing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _backups.ReplaceAsync(Arg.Any<Stream>(), Arg.Any<IRestoreProgress>()).Returns(async _ =>
        {
            replacing.SetResult();
            await release.Task;
            return (IReadOnlyList<RestoreAdjustment>)[];
        });
        using var request = new CancellationTokenSource();
        var backup = new TrackedStream([1]);

        var waiting = _service.RestoreAsync(backup, null, null, request.Token);
        await replacing.Task.WaitAsync(Cancellation);
        await request.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        _lock.BusyWith.Should().Be("restauro", "the restore still runs");
        _tracker.Current!.Stage.Should().Be(RestoreStage.Replacing);
        backup.Disposed.Should().BeFalse("the restore still reads it");

        release.SetResult();
        await WaitUntilAsync(() => _lock.BusyWith is null);

        _tracker.Current!.Stage.Should().Be(RestoreStage.Completed);
        backup.Disposed.Should().BeTrue();
        await _backups.Received(1).VerifyAsync(Contents, CancellationToken.None);
        _logger.Entries.Should().Contain(e => e.EventId == 14 && e.Message == "The client stopped waiting for the restore; it continues on the server");
    }

    [Fact]
    public async Task A_new_restore_replaces_the_progress_of_the_last_one()
    {
        _backups.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Result<BackupContents>.Failure(Error.Validation("x")), Result<BackupContents>.Success(Contents));
        await _service.RestoreAsync(OneByte(), null, "first", Cancellation);
        _time.Advance(TimeSpan.FromMinutes(1));

        await _service.RestoreAsync(OneByte(), null, "second", Cancellation);

        _tracker.Current.Should().BeEquivalentTo(new { Id = "second", Stage = RestoreStage.Completed, StartedAt = Now.UtcDateTime.AddMinutes(1), Error = (string?)null });
    }

    private T Call<T>(string name, T value)
    {
        _calls.Add($"{name}:{_lock.BusyWith}");
        return value;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Cancellation);
        }

        condition().Should().BeTrue();
    }

    private static TrackedStream OneByte() => new([0x1F]);

    private sealed class TrackedStream(byte[] content) : MemoryStream(content)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
