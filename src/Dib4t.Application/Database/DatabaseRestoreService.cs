using Dib4t.Application.Common;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Database;

/// <summary>Use case <c>POST /api/database/restore</c> (legacy <c>routes/database.js</c>, PLAN.md §7.6).</summary>
public sealed partial class DatabaseRestoreService(
    IDatabaseBackupService backups,
    DatabaseOperationLock operationLock,
    ILogger<DatabaseRestoreService> logger)
{
    /// <summary>Restores <paramref name="backup"/> (seekable). Legacy order: empty file → 400, busy → 409, then the restore.</summary>
    public async Task<Result<RestoreSummary>> RestoreAsync(Stream backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Length == 0)
        {
            return Error.Validation(BackupMessages.NoFileReceived);
        }

        using var held = operationLock.TryAcquire(DatabaseOperationLock.Restore);
        if (held is null)
        {
            return Error.Conflict(BackupMessages.Busy(operationLock.BusyWith ?? DatabaseOperationLock.Restore));
        }

        LogRestoreStarted(backup.Length);
        try
        {
            var result = await backups.RestoreAsync(backup, cancellationToken);
            if (result.IsSuccess)
            {
                LogRestoreFinished(result.Value.Collections, result.Value.Documents);
            }
            else
            {
                LogRestoreRejected(result.Error.Message);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // legacy: any other failure is a 500 "Erro ao restaurar o backup."
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRestoreFailed(ex);
            return Error.Failure(BackupMessages.RestoreFailed);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Database restore started ({Bytes} bytes)")]
    private partial void LogRestoreStarted(long bytes);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Database restored: {Collections} collections, {Documents} documents")]
    private partial void LogRestoreFinished(int collections, long documents);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Database restore rejected: {Reason}")]
    private partial void LogRestoreRejected(string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Database restore failed")]
    private partial void LogRestoreFailed(Exception exception);
}
