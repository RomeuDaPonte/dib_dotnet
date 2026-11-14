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
}
