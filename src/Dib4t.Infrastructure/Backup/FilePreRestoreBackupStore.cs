using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Dib4t.Application.Database;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Backup;

/// <summary>The server's backup folder (<c>Backup:*</c>, the section of the scheduled backups too; PLAN.md §7.6, §12).</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// Folder of the automatic pre-restore backups: <c>/app/backups</c> in the container (a volume,
    /// <c>appsettings.json</c>), <c>backups</c> next to the server in Development; a relative path is relative to the
    /// working directory. Empty: a folder in the system's temporary directory.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>How many automatic pre-restore backups are kept (the oldest are deleted).</summary>
    [Range(1, 1000)]
    public int PreRestoreKeep { get; set; } = 5;

    /// <summary>The configured folder, or the default one.</summary>
    public string ResolvedDirectory => Path.GetFullPath(
        string.IsNullOrWhiteSpace(Directory) ? Path.Combine(Path.GetTempPath(), "dib4t-backups") : Directory);
}

/// <summary>
/// <see cref="IPreRestoreBackupStore"/> on the file system: <c>dib4t-pre-restore-&lt;UTC time&gt;.dibbak</c>, written
/// under a temporary name and renamed once complete, so the folder never holds a partial backup under a final name.
/// </summary>
internal sealed class FilePreRestoreBackupStore(IOptions<BackupOptions> options) : IPreRestoreBackupStore
{
    public const string Prefix = "dib4t-pre-restore-";

    public async Task<string> SaveAsync(Func<Stream, Task> write, DateTime createdAt)
    {
        ArgumentNullException.ThrowIfNull(write);
        var directory = options.Value.ResolvedDirectory;
        Directory.CreateDirectory(directory);

        var stamp = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture);
        var fileName = $"{Prefix}{stamp}{DibbakFormat.FileExtension}";
        for (var copy = 1; File.Exists(Path.Combine(directory, fileName)); copy++)
        {
            fileName = $"{Prefix}{stamp}-{copy}{DibbakFormat.FileExtension}";
        }

        var partial = Path.Combine(directory, $".{fileName}.partial");
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await write(file);
                await file.FlushAsync();
            }

            File.Move(partial, Path.Combine(directory, fileName));
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        Prune(directory);
        return fileName;
    }

    /// <summary>Keeps the newest <see cref="BackupOptions.PreRestoreKeep"/> automatic backups (the names sort by time).</summary>
    private void Prune(string directory)
    {
        var old = Directory.EnumerateFiles(directory, $"{Prefix}*{DibbakFormat.FileExtension}")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(options.Value.PreRestoreKeep);
        foreach (var file in old)
        {
            File.Delete(file);
        }
    }
}
