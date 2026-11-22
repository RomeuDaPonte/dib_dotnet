using Dib4t.Application.Database;
using Dib4t.Contracts.Database;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Dib4t.Server.Controllers;

/// <summary><c>/api/database</c> (legacy <c>routes/database.js</c>, PLAN.md §6, §7.6).</summary>
[Route("api/database")]
[Authorize(Policy = Policies.CanSeeDefinicoes)]
public sealed class DatabaseController(DatabaseRestoreService restoreService, DatabaseBackupService backupService, TimeProvider time) : ControllerBase
{
    /// <summary>Legacy <c>express.raw({ limit: "1gb" })</c>: 1 GiB.</summary>
    public const long MaxBackupBytes = 1L << 30;

    private const string BackupMediaType = "application/octet-stream";

    /// <summary><c>GET info</c>: the database statistics, <c>fetchedAt</c> and <c>busyWith</c> (legacy <c>{ ...info, busyWith }</c>).</summary>
    [HttpGet("info")]
    public async Task<DatabaseInfoResponse> Info(CancellationToken cancellationToken)
    {
        var (info, fetchedAt, busyWith) = await backupService.GetInfoAsync(cancellationToken);
        return new DatabaseInfoResponse(info.Name, info.Host, info.Port, info.ServerVersion, info.Collections, info.Documents,
            info.AvgObjSize, info.DataSize, info.StorageSize, info.Indexes, info.IndexSize,
            [.. info.CollectionsInfo.Select(c => new CollectionInfoResponse(c.Name, c.Count, c.Size, c.AvgObjSize, c.StorageSize, c.Indexes, c.IndexSize))],
            fetchedAt, busyWith);
    }

    /// <summary>
    /// <c>GET backup</c>: the whole database as a <c>.dibbak</c> download, streamed as it is read (legacy headers:
    /// <c>application/gzip</c>, <c>attachment; filename="dib4t-backup-&lt;ISO time with - for : and .&gt;.dibbak"</c>,
    /// <c>Access-Control-Expose-Headers</c>). Busy → 409. A failure before any byte is sent is the legacy 500
    /// "Erro ao criar o backup." (as plain text: legacy keeps the gzip type and the attachment header on it); after
    /// that the connection is aborted, so the browser reports a failed download instead of saving a cut-off file.
    /// </summary>
    [HttpGet("backup")]
    public async Task<IActionResult> Backup()
    {
        var cancellationToken = HttpContext.RequestAborted;
        var result = await backupService.BackupAsync(StartDownload, User.FindFirst(Dib4tClaims.Email)?.Value ?? User.Identity?.Name, cancellationToken);
        if (result.IsSuccess)
        {
            return new EmptyResult();
        }

        if (Response.HasStarted)
        {
            HttpContext.Abort();
            return new EmptyResult();
        }

        Response.Headers.Remove(HeaderNames.ContentDisposition);
        Response.Headers.Remove(HeaderNames.AccessControlExposeHeaders);
        return result.Error.ToLegacyResult();
    }

    private Stream StartDownload()
    {
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Response.ContentType = "application/gzip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"dib4t-backup-{stamp}.dibbak\"";
        Response.Headers.AccessControlExposeHeaders = HeaderNames.ContentDisposition;
        return Response.Body;
    }

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
