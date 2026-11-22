namespace Dib4t.Contracts.Database;

/// <summary>
/// <c>GET /api/database/restore/status</c> (new app only, PLAN.md §7.6): the running or the last restore since the
/// server started (204 when there was none). <see cref="Stage"/>: <c>validating</c>, <c>preRestoreBackup</c>,
/// <c>replacing</c>, <c>verifying</c> while it runs; then <c>completed</c>, <c>rejected</c> or <c>failed</c>.
/// </summary>
public sealed record RestoreStatusResponse(
    string Id,
    string Stage,
    bool Running,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long Bytes,
    int? Collections,
    long? Documents,
    string? CurrentCollection,
    int CollectionsDone,
    long DocumentsDone,
    string? PreRestoreBackup,
    IReadOnlyList<RestoreAdjustmentResponse> Adjustments,
    RestoreVerificationResponse? Verification,
    string? Error,
    bool DatabaseChanged)
{
    public const string Validating = "validating";
    public const string PreRestoreBackupStage = "preRestoreBackup";
    public const string Replacing = "replacing";
    public const string Verifying = "verifying";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

/// <summary>What the restore left out for MongoDB 9.0 (<see cref="Index"/> <c>null</c>: the collection options).</summary>
public sealed record RestoreAdjustmentResponse(string Collection, string? Index, IReadOnlyList<string> Removed, bool IndexRemoved, string? ServerMessage);

/// <summary>The built-in verification: the restored database compared with the file.</summary>
public sealed record RestoreVerificationResponse(bool HasDifferences, IReadOnlyList<CollectionVerificationResponse> Collections, IReadOnlyList<string> UnexpectedCollections);

/// <summary>One collection of the file compared with the database.</summary>
public sealed record CollectionVerificationResponse(
    string Name,
    bool Matches,
    bool Exists,
    long ExpectedDocuments,
    long Documents,
    bool DocumentsMatch,
    IReadOnlyList<string> MissingIndexes);
