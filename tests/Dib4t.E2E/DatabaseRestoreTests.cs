using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>
/// C3 / PLAN.md §4.0: a backup downloaded from the old app (Settings → Base de dados) imports into the new app
/// through its restore endpoint with every document unchanged (values, BSON types, field order). §7.6: the restore
/// runs on the server, and a page opened or reloaded while it runs follows it to its end.
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
        var status = await Harness.New.RestoreStatusAsync(Cancellation);

        actual.Collections.Keys.Should().BeEquivalentTo(expected.Collections.Keys);
        actual.DocumentCount.Should().Be(Harness.Seed.DocumentCount);
        actual.DiffFrom(expected).Should().BeEmpty("the new app holds exactly what the legacy app exported");
        (status!.Stage, status.Verification!.HasDifferences, status.Adjustments.Count).Should().Be(("completed", false, 0),
            "the built-in verification agrees, and a legacy backup needs no adjustment for MongoDB 9.0");
    }

    [Fact]
    public async Task A_page_reloaded_during_a_restore_follows_it_on_the_server_to_its_end()
    {
        // The seed and a large collection, so the restore takes about ten seconds (longer than a page reload).
        const int Bulk = 1_500_000;
        var seed = DibbakFile.Read(Harness.Options.SeedBackup);
        var writer = new DibbakTestWriter().Magic().HeaderFrame();
        foreach (var collection in seed.Collections)
        {
            writer.CollectionFrame(collection.Name, count: collection.Documents.Count);
            collection.Documents.ToList().ForEach(document => writer.Frame(DibbakTestWriter.Document, document));
        }

        writer.CollectionFrame("zzbulk", count: Bulk);
        for (var i = 0; i < Bulk; i++)
        {
            writer.DocumentFrame(new BsonDocument { { "_id", i }, { "n", i } });
        }

        var documents = seed.DocumentCount + Bulk;
        var file = writer.EndFrame(seed.Collections.Count + 1, documents).ToFile();
        try
        {
            await Harness.LoadSeedAsync(Cancellation);
            var session = await OpenAsync(Harness.New);
            await new NewLoginPage(session.Page).GotoAsync();
            await new NewLoginPage(session.Page).LoginAsync("utilizador02@dib4t.local", "dib4t-dev");
            var database = new NewDatabasePage(session.Page);
            await database.GotoAsync();

            var restore = Harness.New.RestoreAsync(file, Cancellation); // from somewhere else (another tab, another user)
            for (var i = 0; i < 100 && await Harness.New.RestoreStatusAsync(Cancellation) is not { Running: true }; i++)
            {
                await Task.Delay(50, Cancellation);
            }

            await database.GotoAsync(); // the page loaded anew (as after a reload), back on the tab
            await database.Overlay.WaitForAsync(new() { Timeout = 30_000 });

            (await database.Overlay.Locator(".database-overlay-title").InnerTextAsync()).Should().Be("A restaurar a base de dados...");
            await database.Result.WaitForAsync(new() { Timeout = 120_000 });
            await restore;
            var lines = (await database.Result.InnerTextAsync()).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            lines.Take(3).Should().Equal(
                "Restauro concluído",
                $"9 coleções, {Dib4t.Client.Settings.DatabaseFormat.Number(documents)} documentos.",
                Dib4t.Client.Settings.RestoreTexts.NoDifferences);
            (await database.Overlay.CountAsync()).Should().Be(0);
            (await database.Notification.Last.InnerTextAsync()).Trim().Should().Be(
                $"Base de dados restaurada: 9 coleções, {Dib4t.Client.Settings.DatabaseFormat.Number(documents)} documentos.");
            session.Errors.Should().BeEmpty();
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation);
        }
    }
}
