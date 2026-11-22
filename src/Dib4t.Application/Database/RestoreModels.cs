namespace Dib4t.Application.Database;

/// <summary>
/// A backup that passed every check of the first pass (PLAN.md §7.6): what the restore will write and what the
/// built-in verification compares the database with afterwards.
/// </summary>
/// <param name="Summary">The legacy answer (header values and totals).</param>
/// <param name="Collections">Every collection of the file, in file order.</param>
/// <param name="Adjustments">What was removed from the file's options and index specs because MongoDB 9.0 rejects it.</param>
public sealed record BackupContents(
    RestoreSummary Summary,
    IReadOnlyList<BackupCollectionContents> Collections,
    IReadOnlyList<RestoreAdjustment> Adjustments);

/// <summary>One collection of the file.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Documents">Number of document frames.</param>
/// <param name="Checksum">
/// Order-independent checksum of the raw BSON bytes of its documents (field names, order, BSON types and values),
/// compared with the same checksum of the restored collection.
/// </param>
/// <param name="Indexes">The indexes the restore creates (after the 9.0 cleanup), without <c>_id_</c>.</param>
public sealed record BackupCollectionContents(string Name, long Documents, UInt128 Checksum, IReadOnlyList<ExpectedIndex> Indexes);

/// <summary>An index the restore creates: its name and its key as canonical text.</summary>
public sealed record ExpectedIndex(string Name, string Key);

/// <summary>
/// Something of the file that MongoDB 9.0 rejects (or that no longer has a meaning) and that the restore left out
/// (PLAN.md §7.6 "Import of legacy backups"). The documents themselves are never changed.
/// </summary>
/// <param name="Collection">The collection.</param>
/// <param name="Index">The index, or <see langword="null"/> for the collection options.</param>
/// <param name="Removed">The option or index fields left out (empty when the whole index was left out).</param>
/// <param name="IndexRemoved">The whole index was left out (a type MongoDB 9.0 can no longer create).</param>
/// <param name="ServerMessage">The server's refusal, when the server rejected what the cleanup kept.</param>
public sealed record RestoreAdjustment(
    string Collection,
    string? Index,
    IReadOnlyList<string> Removed,
    bool IndexRemoved = false,
    string? ServerMessage = null);

/// <summary>The built-in verification: the restored database compared with the file (PLAN.md §2.2 step 3).</summary>
/// <param name="Collections">One entry per collection of the file.</param>
/// <param name="UnexpectedCollections">User collections in the database that the file does not have.</param>
public sealed record RestoreVerification(IReadOnlyList<CollectionVerification> Collections, IReadOnlyList<string> UnexpectedCollections)
{
    public bool HasDifferences => UnexpectedCollections.Count > 0 || Collections.Any(c => !c.Matches);
}

/// <summary>One collection compared with the file.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Exists">The collection is in the database.</param>
/// <param name="ExpectedDocuments">Documents in the file.</param>
/// <param name="Documents">Documents in the database.</param>
/// <param name="DocumentsMatch">The checksums of the documents are equal (every field, order, type and value).</param>
/// <param name="MissingIndexes">Indexes of the file that are not in the database with the same key.</param>
public sealed record CollectionVerification(
    string Name,
    bool Exists,
    long ExpectedDocuments,
    long Documents,
    bool DocumentsMatch,
    IReadOnlyList<string> MissingIndexes)
{
    public bool Matches => Exists && ExpectedDocuments == Documents && DocumentsMatch && MissingIndexes.Count == 0;
}

/// <summary>Where a restore is (PLAN.md §7.6: progress that can be read again).</summary>
public enum RestoreStage
{
    /// <summary>The whole file is read and checked; nothing written yet.</summary>
    Validating,

    /// <summary>The current database is saved to the server's backup folder; nothing changed yet.</summary>
    PreRestoreBackup,

    /// <summary>The user collections are dropped and recreated from the file.</summary>
    Replacing,

    /// <summary>The restored database is compared with the file.</summary>
    Verifying,

    /// <summary>Restored (see the verification).</summary>
    Completed,

    /// <summary>The file was refused (legacy message); the database is unchanged.</summary>
    Rejected,

    /// <summary>Something failed (see <see cref="RestoreStatus.DatabaseChanged"/>).</summary>
    Failed,
}

/// <summary>A snapshot of the running or the last restore since the app started.</summary>
public sealed record RestoreStatus(
    string Id,
    RestoreStage Stage,
    DateTime StartedAt,
    long Bytes,
    string? RestoredBy)
{
    public bool IsRunning => Stage is RestoreStage.Validating or RestoreStage.PreRestoreBackup or RestoreStage.Replacing or RestoreStage.Verifying;

    public DateTime? FinishedAt { get; init; }

    /// <summary>Collections in the file (known once it is validated).</summary>
    public int? Collections { get; init; }

    /// <summary>Documents in the file (known once it is validated).</summary>
    public long? Documents { get; init; }

    /// <summary>The collection being written while <see cref="RestoreStage.Replacing"/>.</summary>
    public string? CurrentCollection { get; init; }

    /// <summary>Collections started so far (the current one included).</summary>
    public int CollectionsDone { get; init; }

    /// <summary>Documents written so far.</summary>
    public long DocumentsDone { get; init; }

    /// <summary>File name of the automatic backup of the database as it was before (server's backup folder).</summary>
    public string? PreRestoreBackup { get; init; }

    public IReadOnlyList<RestoreAdjustment> Adjustments { get; init; } = [];

    public RestoreVerification? Verification { get; init; }

    /// <summary>The legacy message of a rejected file or a failure.</summary>
    public string? Error { get; init; }

    /// <summary>A failure after the user collections started to be replaced: the database is incomplete.</summary>
    public bool DatabaseChanged { get; init; }
}

/// <summary>Receives the progress of the replace step.</summary>
public interface IRestoreProgress
{
    /// <summary><paramref name="collection"/> is being written (the <paramref name="collectionNumber"/>-th); <paramref name="documents"/> written in all so far.</summary>
    void Replacing(string collection, int collectionNumber, long documents);
}
