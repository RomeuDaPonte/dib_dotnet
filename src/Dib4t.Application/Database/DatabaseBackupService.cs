using Dib4t.Application.Common;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Database;

/// <summary>
/// Use cases <c>GET /api/database/info</c> and <c>GET /api/database/backup</c> (legacy <c>routes/database.js</c>,
/// PLAN.md §7.6). The backup holds the <see cref="DatabaseOperationLock"/> while it is written, like the restore.
/// </summary>
public sealed partial class DatabaseBackupService(
    IDatabaseBackupService backups,
    DatabaseOperationLock operationLock,
    TimeProvider time,
    ILogger<DatabaseBackupService> logger)
{
    /// <summary>The database statistics, when they were read and the running operation (legacy <c>{ ...info, busyWith }</c>).</summary>
    public async Task<DatabaseInfoSnapshot> GetInfoAsync(CancellationToken cancellationToken)
    {
        var info = await backups.GetInfoAsync(cancellationToken);
        return new DatabaseInfoSnapshot(info, time.GetUtcNow().UtcDateTime, operationLock.BusyWith);
    }

    /// <summary>
    /// Writes a backup to the stream <paramref name="openOutput"/> gives. Legacy order: busy → 409 before anything is
    /// sent; then <paramref name="openOutput"/> (the caller sets the download headers there) and the backup. Any
    /// failure, a client that went away included, is a <see cref="ErrorKind.Failure"/> "Erro ao criar o backup."; the
    /// caller aborts the response if part of the file was already sent (legacy <c>res.destroy</c>), so the client never
    /// keeps a truncated file that looks complete.
    /// </summary>
    public async Task<Result<BackupSummary>> BackupAsync(Func<Stream> openOutput, string? createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openOutput);
        using var held = operationLock.TryAcquire(DatabaseOperationLock.Backup);
        if (held is null)
        {
            return Error.Conflict(BackupMessages.Busy(operationLock.BusyWith ?? DatabaseOperationLock.Backup));
        }

        LogBackupStarted(createdBy);
        try
        {
            var summary = await backups.WriteBackupAsync(openOutput(), createdBy, time.GetUtcNow().UtcDateTime, cancellationToken);
            LogBackupWritten(summary.Collections, summary.Documents);
            return summary;
        }
#pragma warning disable CA1031 // legacy: any failure is "Erro ao criar o backup." (or a cut connection)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (cancellationToken.IsCancellationRequested)
            {
                LogBackupCancelled();
            }
            else
            {
                LogBackupFailed(ex);
            }

            return Error.Failure(BackupMessages.BackupFailed) with { Reason = ex.Message };
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Database backup started (by {CreatedBy})")]
    private partial void LogBackupStarted(string? createdBy);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Database backup completed: {Collections} collections, {Documents} documents")]
    private partial void LogBackupWritten(int collections, long documents);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Database backup cancelled: the client went away")]
    private partial void LogBackupCancelled();

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Database backup failed")]
    private partial void LogBackupFailed(Exception exception);
}
