using Dib4t.Application.Database;
using Dib4t.Contracts.Database;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Dib4t.Server.Controllers;

/// <summary><c>/api/database</c> (legacy <c>routes/database.js</c>, PLAN.md §6, §7.6).</summary>
[Route("api/database")]
[BlockedInProductionUntilAuth]
public sealed class DatabaseController(DatabaseRestoreService restoreService) : ControllerBase
{
    /// <summary>Legacy <c>express.raw({ limit: "1gb" })</c>: 1 GiB.</summary>
    public const long MaxBackupBytes = 1L << 30;

    private const string BackupMediaType = "application/octet-stream";

    /// <summary>
    /// Replaces the database with an uploaded <c>.dibbak</c> (raw body, <c>application/octet-stream</c>). The upload
    /// is spooled to a temporary file (deleted when the request ends), so the restore can read it twice without
    /// holding it in memory.
    /// </summary>
    [HttpPost("restore")]
    [RequestSizeLimit(MaxBackupBytes)]
    public async Task<IActionResult> Restore(CancellationToken cancellationToken)
    {
        // Legacy: a body of another content type is not parsed, so it counts as "no file" (400).
        if (!IsOctetStream(Request.ContentType))
        {
            return await RestoreAsync(Stream.Null, cancellationToken);
        }

        await using var upload = new FileStream(
            Path.Combine(Path.GetTempPath(), $"dib4t-restore-{Guid.NewGuid():N}.dibbak"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await Request.Body.CopyToAsync(upload, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return LegacyResults.Text(StatusCodes.Status413PayloadTooLarge, "request entity too large");
        }

        upload.Position = 0;
        return await RestoreAsync(upload, cancellationToken);
    }

    private async Task<IActionResult> RestoreAsync(Stream backup, CancellationToken cancellationToken)
    {
        var result = await restoreService.RestoreAsync(backup, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToLegacyResult();
        }

        var summary = result.Value;
        return Ok(new RestoreResponse(summary.BackupCreatedAt, summary.BackupDatabase, summary.Collections, summary.Documents));
    }

    /// <summary>body-parser's <c>type-is</c> match: the media type, case-insensitive, parameters ignored.</summary>
    private static bool IsOctetStream(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals(BackupMediaType, StringComparison.OrdinalIgnoreCase);
}
