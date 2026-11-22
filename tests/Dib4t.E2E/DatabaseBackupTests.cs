using System.Globalization;
using System.Text.RegularExpressions;
using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;

namespace Dib4t.E2E;

/// <summary>
/// Settings → Base de Dados (PLAN.md §7.6, §10 Phase 5 step 3) in both UIs, each app on its own database loaded from
/// the same seed: the same statistics labels, counts and collection rows; "Transferir Backup" saves a file holding
/// exactly the seed, named after the browser's local time; "Restaurar Backup" with the seed file asks the same
/// confirmation, says the same and leaves exactly the seed; a file that is not a backup is refused with the same texts.
/// </summary>
public sealed partial class DatabaseBackupTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    [Fact]
    public async Task Both_uis_show_the_same_database_information()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var shown = new List<(string App, IReadOnlyList<(string Label, string Value, string Detail)> Stats, IReadOnlyList<IReadOnlyList<string>> Table)>();
        foreach (var app in Harness.Apps)
        {
            var (session, database) = await OpenDatabaseAsync(app);
            shown.Add((app.Name, await database.StatsAsync(), await database.TableAsync()));
            session.Errors.Should().BeEmpty(app.Name);
        }

        var (legacy, created) = (shown[0], shown[1]);
        created.Stats.Select(s => s.Label).Should().Equal(legacy.Stats.Select(s => s.Label)).And.Equal(
            "BASE DE DADOS", "VERSÃO MONGODB", "COLEÇÕES", "DOCUMENTOS", "TAMANHO DOS DADOS", "ARMAZENAMENTO EM DISCO", "ÍNDICES");
        // Same database name, collections, documents and indexes; the server (host, version) and its sizes differ.
        foreach (var label in new[] { "BASE DE DADOS", "COLEÇÕES", "DOCUMENTOS", "ÍNDICES" })
        {
            Stat(created, label).Value.Should().Be(Stat(legacy, label).Value, label);
        }

        Stat(created, "DOCUMENTOS").Value.Should().Be("30 754");
        (Stat(legacy, "VERSÃO MONGODB").Value, Stat(created, "VERSÃO MONGODB").Value).Should().Be(("4.1.4", "9.0.8"));
        Stat(created, "DOCUMENTOS").Detail.Should().MatchRegex(@"^Tamanho médio \d+(\.\d+)? (B|KB)$");
        created.Table[0].Should().Equal(legacy.Table[0]).And.Equal("Coleção", "Documentos", "Tamanho", "Tamanho médio", "Armazenamento", "Índices");
        created.Table.Skip(1).Select(Comparable).Should().Equal(legacy.Table.Skip(1).Select(Comparable));
        created.Table.Skip(1).Select(r => r[0]).Should().Equal("budgets", "entities", "jobs", "paymentmethods", "prices", "printersettings", "tasks", "users");
    }

    [Fact]
    public async Task A_backup_downloaded_from_either_ui_holds_exactly_the_seed()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seed = Harness.Seed.ToSnapshot();
        foreach (var app in Harness.Apps)
        {
            var (session, database) = await OpenDatabaseAsync(app);
            var before = DateTime.UtcNow.AddSeconds(-2);

            var notifications = await database.Notification.CountAsync();
            var (fileName, content) = await database.DownloadAsync();

            fileName.Should().MatchRegex(@"^dib4t-backup-\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d\.dibbak$", app.Name);
            var lisbon = TimeZoneInfo.ConvertTimeToUtc(DateTime.ParseExact(fileName[13..32], "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon"));
            lisbon.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(2), $"{app.Name}: the browser's local time (Europe/Lisbon)");
            var backup = DibbakFile.Parse(content);
            backup.ToSnapshot().DiffFrom(seed).Should().BeEmpty($"{app.Name}: the file holds exactly the database");
            backup.Header.GetProperty("createdBy").GetString().Should().Be(FixtureUser, app.Name);
            (await NotificationAsync(database, notifications)).Should().Be($"Backup criado com sucesso ({Bytes(content.Length)})", app.Name);
            (await Unexpected(database).CountAsync()).Should().Be(0, app.Name);
            session.Errors.Should().BeEmpty(app.Name);
        }
    }

    [Fact]
    public async Task Restoring_the_seed_file_through_either_ui_asks_and_says_the_same_and_leaves_exactly_the_seed()
    {
        var results = new List<(string App, IReadOnlyList<string> Dialog, string Message)>();
        IReadOnlyList<string> resultDialog = [];
        DbSnapshot? beforeRestore = null;
        try
        {
            foreach (var app in Harness.Apps)
            {
                await Harness.LoadSeedAsync(Cancellation);
                var (session, database) = await OpenDatabaseAsync(app);
                if (app == Harness.New)
                {
                    // Something to replace: the restore must bring the prices back.
                    await Harness.New.Database.GetCollection<MongoDB.Bson.BsonDocument>("prices").DeleteManyAsync(
                        MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty, Cancellation);
                }

                await database.ChooseFileAsync(Harness.Options.SeedBackup);
                await database.Dialog.WaitForAsync();
                var dialog = Lines(await database.Dialog.InnerTextAsync());
                var before = await database.Notification.CountAsync();
                if (app == Harness.New)
                {
                    beforeRestore = await app.SnapshotAsync(Cancellation);
                }

                await database.ConfirmAsync();
                var message = await NotificationAsync(database, before, TimeSpan.FromMinutes(2));
                if (database is NewDatabasePage newPage)
                {
                    await newPage.Result.WaitForAsync();
                    resultDialog = Lines(await newPage.Result.InnerTextAsync());
                }

                results.Add((app.Name, dialog, message));
                (await app.SnapshotAsync(Cancellation)).DiffFrom(Harness.Seed.ToSnapshot()).Should().BeEmpty($"{app.Name}: the database is the restored file");
                session.Errors.Should().BeEmpty(app.Name);
            }
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation);
        }

        var (legacy, created) = (results[0], results[1]);
        var modified = File.GetLastWriteTime(Harness.Options.SeedBackup).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        // The one line the new app adds (PLAN.md §7.6: the automatic pre-restore backup), after the legacy warning.
        created.Dialog.Should().Equal(legacy.Dialog.Take(2).Append(Dib4t.Client.Settings.RestoreTexts.SafetyNote).Concat(legacy.Dialog.Skip(2)));
        created.Dialog.Should().Contain("Restaurar backup").And.Contain($"Ficheiro: {Path.GetFileName(Harness.Options.SeedBackup)}")
            .And.Contain($"Tamanho: {Bytes(new FileInfo(Harness.Options.SeedBackup).Length)}");
        created.Dialog.Should().Contain(l => l.StartsWith("Modificado: ", StringComparison.Ordinal) && (TimeZoneInfo.Local.Id != "Europe/Lisbon" || l == $"Modificado: {modified}"));
        created.Message.Should().Be(legacy.Message).And.Be($"Base de dados restaurada: 8 coleções, {Harness.Seed.DocumentCount.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ", StringComparison.Ordinal)} documentos.");
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_refused_with_the_same_texts_and_nothing_changes()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var directory = Directory.CreateTempSubdirectory("dib4t-e2e-not-a-backup-");
        try
        {
            var text = Path.Combine(directory.FullName, "notas.txt");
            var garbage = Path.Combine(directory.FullName, "garbage.dibbak");
            await File.WriteAllTextAsync(text, "not a backup", Cancellation);
            await File.WriteAllTextAsync(garbage, "this is not a backup", Cancellation);
            var messages = new List<(string App, string Extension, string Content)>();
            foreach (var app in Harness.Apps)
            {
                var (session, database) = await OpenDatabaseAsync(app);

                await database.ChooseFileAsync(text);
                var extension = await NotificationAsync(database, 0);
                (await database.Dialog.CountAsync()).Should().Be(0, app.Name);
                await database.ChooseFileAsync(garbage);
                await database.Dialog.WaitForAsync();
                var before = await database.Notification.CountAsync();
                await database.ConfirmAsync();
                var content = await NotificationAsync(database, before);

                messages.Add((app.Name, extension, content));
                (await Unexpected(database).CountAsync()).Should().Be(0, $"{app.Name}: a 400 is not an unexpected error");
                (await app.SnapshotAsync(Cancellation)).DiffFrom(Harness.Seed.ToSnapshot()).Should().BeEmpty(app.Name);
                session.Errors.Where(e => !e.Contains("400", StringComparison.Ordinal)).Should().BeEmpty(app.Name);
            }

            messages[1].Should().Be(messages[0] with { App = "new" });
            messages[1].Should().Be(("new", "Selecione um ficheiro de backup (.dibbak).", "O ficheiro não é um backup válido da Dib4t."));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task<(BrowserSession Session, IDatabasePage Database)> OpenDatabaseAsync(IAppUnderTest app)
    {
        var session = await OpenAsync(app);
        IDatabasePage database;
        if (app == Harness.Legacy)
        {
            await new LegacyLoginPage(session.Page).GotoAsync();
            await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            database = new LegacyDatabasePage(session.Page);
        }
        else
        {
            await new NewLoginPage(session.Page).GotoAsync();
            await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            database = new NewDatabasePage(session.Page);
        }

        await database.GotoAsync();
        return (session, database);
    }

    /// <summary>The text of the notification after the first <paramref name="before"/> ones, once it shows.</summary>
    private static async Task<string> NotificationAsync(IDatabasePage database, int before, TimeSpan? timeout = null)
    {
        var next = database.Notification.Nth(before);
        await next.WaitForAsync(new() { Timeout = (float)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds });
        return (await next.InnerTextAsync()).Trim();
    }

    private static Microsoft.Playwright.ILocator Unexpected(IDatabasePage database) =>
        database.Notification.Filter(new() { HasTextString = "An anexpected error occurred" });

    private static (string Label, string Value, string Detail) Stat((string App, IReadOnlyList<(string Label, string Value, string Detail)> Stats, IReadOnlyList<IReadOnlyList<string>> Table) shown, string label) =>
        shown.Stats.Single(s => s.Label == label);

    /// <summary>A collection row without the sizes (each server stores differently): name, documents, number of indexes.</summary>
    private static string Comparable(IReadOnlyList<string> row) => $"{row[0]}|{row[1]}|{row[5].Split(' ')[0]}";

    private static IReadOnlyList<string> Lines(string text) =>
        [.. text.Split('\n').Select(l => Whitespace().Replace(l, " ").Trim()).Where(l => l.Length > 0)];

    /// <summary>Legacy <c>formatBytes</c> for the sizes the scenarios check.</summary>
    private static string Bytes(long bytes) => Dib4t.Client.Settings.DatabaseFormat.Bytes(bytes);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
