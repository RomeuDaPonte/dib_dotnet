using Dib4t.E2E.Harness;

namespace Dib4t.E2E;

/// <summary>
/// C3 / PLAN.md §4.0: a backup downloaded from the old app (Settings → Base de dados) imports into the new app
/// through its restore endpoint with every document unchanged (values, BSON types, field order).
/// </summary>
public sealed class DatabaseRestoreTests(E2EHarness harness) : E2ETest(harness)
{
    [Fact]
    public async Task Backup_downloaded_from_the_legacy_app_restores_unchanged_in_the_new_app()
    {
        var backup = await Harness.Legacy.DownloadBackupAsync(Cancellation);
        var expected = DibbakFile.Parse(backup).ToSnapshot();

        await Harness.New.RestoreAsync(backup, Cancellation);
        var actual = await Harness.New.SnapshotAsync(Cancellation);

        actual.Collections.Keys.Should().BeEquivalentTo(expected.Collections.Keys);
        actual.DocumentCount.Should().Be(Harness.Seed.DocumentCount);
        actual.DiffFrom(expected).Should().BeEmpty("the new app holds exactly what the legacy app exported");
    }
}
