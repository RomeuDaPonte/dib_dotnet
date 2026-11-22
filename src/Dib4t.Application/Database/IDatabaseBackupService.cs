using Dib4t.Application.Common;

namespace Dib4t.Application.Database;

/// <summary>Port: the <c>.dibbak</c> backup format and the database it is restored into (PLAN.md §7.6).</summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// Validates the whole backup and only then replaces every user collection with its content (raw BSON, no
    /// re-serialization, §4.0). A rejected file returns a <see cref="ErrorKind.Validation"/> error with the legacy
    /// message and leaves the database unchanged.
    /// </summary>
    /// <param name="backup">The uploaded file (gzip). Must be seekable: it is read twice (validate, then restore).</param>
    /// <param name="cancellationToken">Cancels the validation only; once the database is being replaced the restore runs to the end.</param>
    Task<Result<RestoreSummary>> RestoreAsync(Stream backup, CancellationToken cancellationToken);

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
