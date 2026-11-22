using Dib4t.Infrastructure.Backup;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>The server's folder of automatic pre-restore backups (PLAN.md §7.6): final names only for complete files, the last N kept.</summary>
public sealed class FilePreRestoreBackupStoreTests : IDisposable
{
    private static readonly DateTime CreatedAt = new(2026, 11, 22, 10, 15, 30, 123, DateTimeKind.Utc);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("dib4t-store-test-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task The_file_is_named_after_the_utc_time_and_holds_what_was_written()
    {
        var name = await Store().SaveAsync(output => output.WriteAsync("backup"u8.ToArray()).AsTask(), CreatedAt);

        name.Should().Be("dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak");
        (await File.ReadAllBytesAsync(Path.Combine(_directory.FullName, name), TestContext.Current.CancellationToken)).Should().Equal("backup"u8.ToArray());
        _directory.GetFiles().Select(f => f.Name).Should().Equal(name);
    }

    [Fact]
    public async Task A_failed_write_leaves_no_file()
    {
        var act = () => Store().SaveAsync(async output =>
        {
            await output.WriteAsync(new byte[1000]);
            throw new IOException("disk full");
        }, CreatedAt);

        await act.Should().ThrowAsync<IOException>();
        _directory.GetFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_newest_automatic_backups_are_kept_and_other_files_are_left_alone()
    {
        var other = Path.Combine(_directory.FullName, "dib4t-backup-2026-01-01_10-00-00.dibbak");
        await File.WriteAllTextAsync(other, "a backup the owner put here", TestContext.Current.CancellationToken);
        var store = Store(keep: 2);

        var names = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            names.Add(await store.SaveAsync(_ => Task.CompletedTask, CreatedAt.AddMinutes(i)));
        }

        _directory.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).Should().Equal(
            "dib4t-backup-2026-01-01_10-00-00.dibbak", names[2], names[3]);
    }

    [Fact]
    public async Task Two_backups_in_the_same_millisecond_get_their_own_names()
    {
        var store = Store();

        var first = await store.SaveAsync(_ => Task.CompletedTask, CreatedAt);
        var second = await store.SaveAsync(_ => Task.CompletedTask, CreatedAt);

        (first, second).Should().Be(("dib4t-pre-restore-2026-11-22T10-15-30-123Z.dibbak", "dib4t-pre-restore-2026-11-22T10-15-30-123Z-1.dibbak"));
    }

    [Fact]
    public async Task The_folder_is_created_when_it_does_not_exist()
    {
        var nested = Path.Combine(_directory.FullName, "a", "b");

        var name = await Store(directory: nested).SaveAsync(_ => Task.CompletedTask, CreatedAt);

        File.Exists(Path.Combine(nested, name)).Should().BeTrue();
    }

    [Fact]
    public void Without_a_folder_the_default_is_in_the_temporary_directory()
    {
        new BackupOptions().ResolvedDirectory.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dib4t-backups")));
        new BackupOptions { Directory = "/app/backups" }.ResolvedDirectory.Should().Be(Path.GetFullPath("/app/backups"));
    }

    private FilePreRestoreBackupStore Store(int keep = 5, string? directory = null) =>
        new(Options.Create(new BackupOptions { Directory = directory ?? _directory.FullName, PreRestoreKeep = keep }));
}
