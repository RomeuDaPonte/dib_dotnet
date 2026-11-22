using Dib4t.Application.Common;

namespace Dib4t.Application.Database;

/// <summary>Port: the <c>.dibbak</c> backup format and the database it is restored into (PLAN.md §7.6).</summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// First restore pass: reads and checks the whole backup; nothing is written. A rejected file returns a
    /// <see cref="ErrorKind.Validation"/> error with the legacy message. The options and index specs MongoDB 9.0 rejects
    /// are left out of what <see cref="ReplaceAsync"/> creates and listed in <see cref="BackupContents.Adjustments"/>.
    /// </summary>
    /// <param name="backup">The uploaded file (gzip), seekable: <see cref="ReplaceAsync"/> reads it again.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<Result<BackupContents>> ValidateAsync(Stream backup, CancellationToken cancellationToken);

    /// <summary>
    /// Second pass, only after <see cref="ValidateAsync"/> accepted the same file: drops every user collection and
    /// recreates each one from the file (raw BSON, no re-serialization, §4.0). It cannot be cancelled: once the
    /// database is being replaced the restore runs to the end. An option or index the server still refuses is left
    /// out (the documents are always written) and returned, with the server's message.
    /// </summary>
    Task<IReadOnlyList<RestoreAdjustment>> ReplaceAsync(Stream backup, IRestoreProgress progress);

    /// <summary>Re-reads the database and compares it with the file: collections, counts, document checksums, indexes.</summary>
    Task<RestoreVerification> VerifyAsync(BackupContents contents, CancellationToken cancellationToken);

    /// <summary>Statistics of the database and of each user collection (legacy <c>getInfo</c>), in the legacy collection order.</summary>
    Task<DatabaseInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a full <c>.dibbak</c> backup (gzip) of every user collection to <paramref name="output"/> (legacy
    /// <c>writeBackup</c>): documents as raw BSON in natural order, nothing re-serialized. Throws if anything fails;
    /// the caller must then make sure the client does not keep the partial file.
    /// </summary>
    /// <param name="output">Written to as the backup is read (never buffered whole); not closed.</param>
    /// <param name="createdBy">The header's <c>createdBy</c> (the user's e-mail, else name), or <see langword="null"/>.</param>
    /// <param name="createdAt">The header's <c>createdAt</c>.</param>
    /// <param name="cancellationToken">Stops the backup (the client went away).</param>
    Task<BackupSummary> WriteBackupAsync(Stream output, string? createdBy, DateTime createdAt, CancellationToken cancellationToken);
}

/// <summary>
/// Port: the server's backup folder, where the automatic backup made before every restore is kept (PLAN.md §7.6;
/// <c>/app/backups</c> in the container).
/// </summary>
public interface IPreRestoreBackupStore
{
    /// <summary>
    /// Creates a new backup file for <paramref name="createdAt"/> and lets <paramref name="write"/> fill it. The file
    /// gets its final name only once <paramref name="write"/> succeeded (a failed or partial write leaves nothing);
    /// older automatic backups beyond the configured number are deleted. Returns the file name.
    /// </summary>
    Task<string> SaveAsync(Func<Stream, Task> write, DateTime createdAt);
}
