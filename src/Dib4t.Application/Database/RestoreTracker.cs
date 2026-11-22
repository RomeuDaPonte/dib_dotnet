namespace Dib4t.Application.Database;

/// <summary>
/// The running or last restore since the app started, readable again at any time (<c>GET
/// /api/database/restore/status</c>, PLAN.md §7.6): a browser that was closed or reloaded during a restore finds it
/// here. Singleton; every change replaces the immutable <see cref="RestoreStatus"/>.
/// </summary>
public sealed class RestoreTracker(TimeProvider time) : IRestoreProgress
{
    private readonly Lock _gate = new();
    private RestoreStatus? _current;

    /// <summary>The running or last restore, or <see langword="null"/> when none ran since the app started.</summary>
    public RestoreStatus? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    internal void Start(string id, long bytes, string? restoredBy) =>
        Set(new RestoreStatus(id, RestoreStage.Validating, time.GetUtcNow().UtcDateTime, bytes, restoredBy));

    internal void Validated(BackupContents contents) => Update(s => s with
    {
        Stage = RestoreStage.PreRestoreBackup,
        Collections = contents.Summary.Collections,
        Documents = contents.Summary.Documents,
        Adjustments = contents.Adjustments,
    });

    internal void PreRestoreBackupSaved(string fileName) =>
        Update(s => s with { Stage = RestoreStage.Replacing, PreRestoreBackup = fileName });

    public void Replacing(string collection, int collectionNumber, long documents) =>
        Update(s => s with { CurrentCollection = collection, CollectionsDone = collectionNumber, DocumentsDone = documents });

    internal void Verifying(IReadOnlyList<RestoreAdjustment> refused) => Update(s => s with
    {
        Stage = RestoreStage.Verifying,
        Adjustments = [.. s.Adjustments, .. refused],
        CurrentCollection = null,
        CollectionsDone = s.Collections ?? s.CollectionsDone,
        DocumentsDone = s.Documents ?? s.DocumentsDone,
    });

    internal void Completed(RestoreVerification verification) =>
        Update(s => s with { Stage = RestoreStage.Completed, Verification = verification, FinishedAt = Now });

    internal void Rejected(string message) =>
        Update(s => s with { Stage = RestoreStage.Rejected, Error = message, FinishedAt = Now });

    internal void Failed(string message) => Update(s => s with
    {
        Stage = RestoreStage.Failed,
        Error = message,
        FinishedAt = Now,
        DatabaseChanged = s.Stage is RestoreStage.Replacing or RestoreStage.Verifying,
    });

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Set(RestoreStatus status)
    {
        lock (_gate)
        {
            _current = status;
        }
    }

    private void Update(Func<RestoreStatus, RestoreStatus> change)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = change(_current);
            }
        }
    }
}
