using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Database;

/// <summary>
/// Use case <c>POST /api/database/restore</c> (legacy <c>routes/database.js</c>, PLAN.md §7.6), hardened for legacy
/// imports: (1) the whole file is checked, (2) the current database is saved to the server's backup folder,
/// (3) the user collections are replaced, (4) the result is compared with the file (built-in verification).
/// </summary>
/// <remarks>
/// The restore runs on the server, not in the request: a browser that closes or loses the connection only stops
/// waiting, the restore goes on and its progress stays readable in <see cref="RestoreTracker"/>. Singleton: the
/// port is resolved in a scope of the restore's own.
/// </remarks>
public sealed partial class DatabaseRestoreService(
    IServiceScopeFactory scopes,
    DatabaseOperationLock operationLock,
    RestoreTracker tracker,
    UserSessionCache sessions,
    TimeProvider time,
    ILogger<DatabaseRestoreService> logger)
{
    /// <summary>
    /// Starts a restore of <paramref name="backup"/> (seekable; this service owns it from now on and disposes it when
    /// the restore ends) and waits for its result. Legacy order: empty file → 400, busy → 409, then the restore.
    /// </summary>
    /// <param name="backup">The uploaded file.</param>
    /// <param name="restoredBy">Who restores (the pre-restore backup's <c>createdBy</c> and the log).</param>
    /// <param name="id">The client's id for this restore (to find it again in the status), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting only: the restore itself continues.</param>
    public async Task<Result<RestoreSummary>> RestoreAsync(Stream backup, string? restoredBy, string? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        IDisposable? held = null;
        try
        {
            if (backup.Length == 0)
            {
                return Error.Validation(BackupMessages.NoFileReceived);
            }

            held = operationLock.TryAcquire(DatabaseOperationLock.Restore);
            if (held is null)
            {
                return Error.Conflict(BackupMessages.Busy(operationLock.BusyWith ?? DatabaseOperationLock.Restore));
            }
        }
        finally
        {
            if (held is null)
            {
                await backup.DisposeAsync();
            }
        }

        tracker.Start(string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id, backup.Length, restoredBy);
        LogRestoreStarted(backup.Length, restoredBy);
        var restore = Task.Run(() => RunAsync(backup, held, restoredBy), CancellationToken.None);
        try
        {
            return await restore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogClientGone();
            throw;
        }
    }

    private async Task<Result<RestoreSummary>> RunAsync(Stream backup, IDisposable held, string? restoredBy)
    {
        using var lockHeld = held;
        await using var file = backup;
        await using var scope = scopes.CreateAsyncScope();
        var backups = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
        var store = scope.ServiceProvider.GetRequiredService<IPreRestoreBackupStore>();
        var stage = RestoreStage.Validating;
        try
        {
            var validated = await backups.ValidateAsync(backup, CancellationToken.None);
            if (!validated.IsSuccess)
            {
                tracker.Rejected(validated.Error.Message);
                LogRestoreRejected(validated.Error.Message);
                return validated.Error;
            }

            var contents = validated.Value;
            tracker.Validated(contents);
            contents.Adjustments.ToList().ForEach(LogAdjustment);

            stage = RestoreStage.PreRestoreBackup;
            var createdAt = time.GetUtcNow().UtcDateTime;
            var saved = await store.SaveAsync(output => backups.WriteBackupAsync(output, restoredBy, createdAt, CancellationToken.None), createdAt);
            LogPreRestoreBackupSaved(saved);
            tracker.PreRestoreBackupSaved(saved);

            stage = RestoreStage.Replacing;
            IReadOnlyList<RestoreAdjustment> refused;
            try
            {
                using var frozen = sessions.Freeze(); // users is empty meanwhile: the signed-in users stay signed in
                refused = await backups.ReplaceAsync(backup, tracker);
            }
            finally
            {
                sessions.Clear(); // the users were replaced (or partly, on a failure): read again on the next request (§4.6)
            }
            refused.ToList().ForEach(LogAdjustment);

            stage = RestoreStage.Verifying;
            tracker.Verifying(refused);
            var verification = await backups.VerifyAsync(contents, CancellationToken.None);
            if (verification.HasDifferences)
            {
                LogVerificationDifferences(string.Join("; ", Describe(verification)));
            }
            else
            {
                LogVerified(contents.Summary.Collections, contents.Summary.Documents);
            }

            tracker.Completed(verification);
            LogRestoreFinished(contents.Summary.Collections, contents.Summary.Documents);
            return contents.Summary;
        }
#pragma warning disable CA1031 // legacy: any other failure is a 500 "Erro ao restaurar o backup."
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (stage is RestoreStage.Validating or RestoreStage.PreRestoreBackup)
            {
                LogRestoreFailedUnchanged(stage, ex);
            }
            else
            {
                LogRestoreFailedChanged(stage, tracker.Current?.PreRestoreBackup, ex);
            }

            tracker.Failed(BackupMessages.RestoreFailed);
            return Error.Failure(BackupMessages.RestoreFailed) with { Reason = ex.Message };
        }
    }

    private void LogAdjustment(RestoreAdjustment adjustment)
    {
        var target = adjustment.Index is null ? "options" : $"index {adjustment.Index}";
        var what = adjustment.IndexRemoved ? "index left out" : $"left out {string.Join(", ", adjustment.Removed)}";
        LogAdjusted(adjustment.Collection, target, adjustment.ServerMessage is { } refusal ? $"{what} (the server refused it: {refusal})" : what);
    }

    /// <summary>The differences in short English text (the log; the page shows its own Portuguese texts).</summary>
    internal static IEnumerable<string> Describe(RestoreVerification verification)
    {
        foreach (var c in verification.Collections.Where(c => !c.Matches))
        {
            if (!c.Exists)
            {
                yield return $"{c.Name}: missing";
                continue;
            }

            if (c.Documents != c.ExpectedDocuments)
            {
                yield return $"{c.Name}: {c.Documents} of {c.ExpectedDocuments} documents";
            }
            else if (!c.DocumentsMatch)
            {
                yield return $"{c.Name}: documents differ from the file";
            }

            if (c.MissingIndexes.Count > 0)
            {
                yield return $"{c.Name}: missing indexes {string.Join(", ", c.MissingIndexes)}";
            }
        }

        foreach (var name in verification.UnexpectedCollections)
        {
            yield return $"{name}: not in the file";
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Database restore started ({Bytes} bytes, by {RestoredBy})")]
    private partial void LogRestoreStarted(long bytes, string? restoredBy);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Database restored: {Collections} collections, {Documents} documents")]
    private partial void LogRestoreFinished(int collections, long documents);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Database restore rejected: {Reason}")]
    private partial void LogRestoreRejected(string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Database restore failed while {Stage}; the database was not changed")]
    private partial void LogRestoreFailedUnchanged(RestoreStage stage, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Database restore failed while {Stage}; the database is incomplete, the previous one is in {PreRestoreBackup}")]
    private partial void LogRestoreFailedChanged(RestoreStage stage, string? preRestoreBackup, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Pre-restore backup saved: {FileName}")]
    private partial void LogPreRestoreBackupSaved(string fileName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Restore adjusted {Collection} {Target} for MongoDB 9.0: {What}")]
    private partial void LogAdjusted(string collection, string target, string what);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Restore verified: no differences ({Collections} collections, {Documents} documents)")]
    private partial void LogVerified(int collections, long documents);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Restore verification found differences: {Differences}")]
    private partial void LogVerificationDifferences(string differences);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "The client stopped waiting for the restore; it continues on the server")]
    private partial void LogClientGone();
}
