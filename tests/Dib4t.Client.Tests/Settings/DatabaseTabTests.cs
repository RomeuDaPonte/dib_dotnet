using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Components.Settings;
using Dib4t.Client.Settings;
using Dib4t.Contracts.Database;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Settings;

/// <summary>The Base de Dados tab (legacy <c>settings/databaseBackup.jsx</c>, PLAN.md §7.6) against substituted API, transfers and clock.</summary>
public sealed class DatabaseTabTests : IAsyncDisposable
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private readonly ClientTestContext _context = new();
    private readonly IDatabaseApi _api = Substitute.For<IDatabaseApi>();
    private readonly IDatabaseTransfers _transfers = Substitute.For<IDatabaseTransfers>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 22, 9, 5, 7, TimeSpan.Zero));
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    public DatabaseTabTests()
    {
        _time.SetLocalTimeZone(Lisbon);
        _context.Services.AddSingleton(_api);
        _context.Services.AddSingleton(_transfers);
        _context.Services.AddSingleton<TimeProvider>(_time);
        _api.InfoAsync().Returns(ApiResult.Success(Info()));
        _transfers.TakeFileAsync(Arg.Any<ElementReference>()).Returns(new ChosenFile("dib4t-backup-2026-11-11_16-15-31.DIBBAK", 1_960_437, 1_794_650_400_123));
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static DatabaseInfoResponse Info(string? busyWith = null, IReadOnlyList<CollectionInfoResponse>? collections = null) => new(
        "dib4t", "localhost", 27018, "9.0.8", collections?.Count ?? 2, 30_754, 412.5, 12_680_000, 4_300_000, 8, 581_632,
        collections ??
        [
            new("budgets", 3_397, 2_500_000, 736, 1_000_000, 1, 81_920),
            new("tasks", 25_971, 8_000_000, 308, 2_000_000, 1, null),
        ],
        new DateTime(2026, 7, 22, 9, 0, 1, DateTimeKind.Utc), busyWith);

    private IRenderedComponent<DatabaseTab> Render()
    {
        _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return _context.Render<DatabaseTab>();
    }

    [Fact]
    public void Shows_the_legacy_statistics_cards_and_collection_rows()
    {
        var tab = Render();

        tab.Find(".database-title").TextContent.Should().Be("Base de Dados");
        tab.Find(".database-fetched").TextContent.Should().Be("Atualizado 22/07/2026 10:00:01", "fetchedAt in the browser's zone (Lisbon summer time)");
        tab.FindAll(".database-stat").Select(Stat).Should().Equal(
            "Base de dados|dib4t|localhost:27018",
            "Versão MongoDB|9.0.8|",
            "Coleções|2|",
            "Documentos|30\u00A0754|Tamanho médio 412.5 B",
            "Tamanho dos dados|12.1 MB|",
            "Armazenamento em disco|4.10 MB|",
            "Índices|8|568.0 KB");
        tab.FindAll(".database-table thead th").Select(c => c.TextContent).Should().Equal("Coleção", "Documentos", "Tamanho", "Tamanho médio", "Armazenamento", "Índices");
        tab.FindAll(".database-table tbody tr").Select(r => string.Join("|", r.QuerySelectorAll("td").Select(c => c.TextContent))).Should().Equal(
            "budgets|3397|2.38 MB|736 B|976.6 KB|1 (80.0 KB)",
            "tasks|25\u00A0971|7.63 MB|308 B|1.91 MB|1");
        tab.FindAll(".database-busy").Should().BeEmpty();
        tab.FindAll("button").Where(b => b.TextContent.Contains("Backup", StringComparison.Ordinal)).Should().OnlyContain(b => !b.HasAttribute("disabled"));
    }

    [Fact]
    public void Without_collStats_and_without_collections()
    {
        _api.InfoAsync().Returns(ApiResult.Success(Info(collections: [])));

        var tab = Render();

        tab.Find(".database-table tbody").TextContent.Should().Be("A base de dados não tem coleções.");
        tab.Find(".database-empty").GetAttribute("colspan").Should().Be("6");
    }

    [Fact]
    public void A_collection_without_collStats_shows_dashes()
    {
        _api.InfoAsync().Returns(ApiResult.Success(Info(collections: [new("jobs", 1_118, null, null, null, 1, null)])));

        var tab = Render();

        tab.Find(".database-table tbody tr").QuerySelectorAll("td").Select(c => c.TextContent).Should().Equal("jobs", "1118", "—", "—", "—", "1");
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restauro")]
    public void An_operation_on_the_server_disables_both_buttons(string running)
    {
        _api.InfoAsync().Returns(ApiResult.Success(Info(busyWith: running)));

        var tab = Render();

        tab.Find(".database-busy").TextContent.Should().Be($"Existe uma operação em curso no servidor ({running}).");
        tab.FindAll(".database-actions button").Should().HaveCount(2).And.OnlyContain(b => b.HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Não tem permissão para concluir esta acção.", "Não tem permissão para concluir esta acção.")]
    [InlineData(HttpStatusCode.InternalServerError, "Something failed.", "Something failed.")]
    [InlineData(null, "", "Erro ao obter informação da base de dados")]
    public void An_info_failure_shows_the_server_text_or_the_legacy_fallback(HttpStatusCode? status, string text, string shown)
    {
        _api.InfoAsync().Returns(ApiResult.Failure<DatabaseInfoResponse>(status, text));

        var tab = Render();

        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal((shown, Severity.Error));
        tab.FindAll(".database-stat").Should().BeEmpty();
    }

    [Fact]
    public async Task Atualizar_reads_the_info_again()
    {
        var tab = Render();
        _api.InfoAsync().Returns(ApiResult.Success(Info(busyWith: "backup")));

        await tab.Find("button[aria-label=Atualizar]").ClickAsync(new MouseEventArgs());

        await _api.Received(2).InfoAsync();
        tab.Find(".database-busy").TextContent.Should().Contain("(backup)");
    }

    [Fact]
    public async Task A_backup_shows_the_overlay_with_the_bytes_received_saves_the_file_and_says_so()
    {
        var download = new TaskCompletionSource<TransferResult>();
        Func<double, Task>? progress = null;
        _transfers.DownloadBackupAsync(Arg.Any<string>(), Arg.Do<Func<double, Task>>(p => progress = p)).Returns(download.Task);
        var tab = Render();

        var click = tab.FindAll(".database-actions button")[0].ClickAsync(new MouseEventArgs());

        tab.WaitForAssertion(() => Overlay(tab).Should().Be("A criar backup da base de dados...|A preparar o ficheiro|Não feche nem atualize esta página."));
        await _transfers.Received(1).GuardUnloadAsync(true);
        await _transfers.Received(1).DownloadBackupAsync("dib4t-backup-2026-07-22_10-05-07.dibbak", Arg.Any<Func<double, Task>>());
        tab.FindAll(".database-actions button").Should().OnlyContain(b => b.HasAttribute("disabled"));
        tab.Find(".database-progress .mud-progress-indeterminate").Should().NotBeNull();

        await tab.InvokeAsync(() => progress!(1_536));
        Overlay(tab).Should().Be("A criar backup da base de dados...|1.50 KB transferidos|Não feche nem atualize esta página.");

        download.SetResult(new TransferResult(200, string.Empty, 1_960_437));
        await click;

        tab.FindAll(".database-overlay").Should().BeEmpty();
        await _transfers.Received(1).GuardUnloadAsync(false);
        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal(("Backup criado com sucesso (1.87 MB)", Severity.Success));
    }

    [Theory]
    [InlineData(409, "Já existe uma operação em curso (restauro). Tente mais tarde.", "Já existe uma operação em curso (restauro). Tente mais tarde.")]
    [InlineData(500, "Erro ao criar o backup.", "Erro ao criar o backup.")]
    [InlineData(0, "", "Erro ao criar o backup")]
    public async Task A_backup_failure_shows_the_server_text_or_the_legacy_fallback(int status, string text, string shown)
    {
        _transfers.DownloadBackupAsync(Arg.Any<string>(), Arg.Any<Func<double, Task>>()).Returns(new TransferResult(status, text));
        var tab = Render();

        await tab.FindAll(".database-actions button")[0].ClickAsync(new MouseEventArgs());

        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal((shown, Severity.Error));
        tab.FindAll(".database-overlay").Should().BeEmpty();
        await _transfers.Received(1).GuardUnloadAsync(false);
    }

    [Fact]
    public async Task Restaurar_Backup_opens_the_file_chooser_and_a_backup_file_asks_for_confirmation()
    {
        var tab = Render();

        await tab.FindAll(".database-actions button")[1].ClickAsync(new MouseEventArgs());
        await _transfers.Received(1).ChooseAsync(Arg.Any<ElementReference>());
        tab.Find("input.database-file").GetAttribute("accept").Should().Be(".dibbak");
        await tab.Find("input.database-file").ChangeAsync(new ChangeEventArgs { Value = "C:\\fakepath\\x.DIBBAK" });

        _dialogs.WaitForAssertion(() => _dialogs.Find(".database-dialog").Should().NotBeNull());
        _dialogs.Find(".settings-dialog-title").TextContent.Should().Be("Restaurar backup");
        _dialogs.Find(".database-warning").TextContent.Should().Contain("Todos os dados atuais da base de dados serão apagados e substituídos pelo conteúdo do backup. Esta ação")
            .And.Contain("Recomenda-se transferir um backup antes de continuar.");
        _dialogs.FindAll(".database-file-box p").Select(p => p.TextContent).Should().Equal(
            "Ficheiro: dib4t-backup-2026-11-11_16-15-31.DIBBAK", "Tamanho: 1.87 MB", "Modificado: 14/11/2026 10:00");
        _dialogs.FindAll(".mud-dialog-actions button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Restaurar");

        await _dialogs.FindAll(".mud-dialog-actions button")[0].ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.FindAll(".database-dialog").Should().BeEmpty());
        await _transfers.Received(1).ForgetFileAsync();
        await _transfers.DidNotReceive().RestoreAsync(Arg.Any<Func<double, double, Task>>());
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_refused_with_the_legacy_text()
    {
        _transfers.TakeFileAsync(Arg.Any<ElementReference>()).Returns(new ChosenFile("notas.txt", 10, 0));
        var tab = Render();

        await tab.Find("input.database-file").ChangeAsync(new ChangeEventArgs());

        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal(("Selecione um ficheiro de backup (.dibbak).", Severity.Error));
        _dialogs.FindAll(".database-dialog").Should().BeEmpty();
        await _transfers.Received(1).ForgetFileAsync();
    }

    [Fact]
    public async Task No_file_chosen_does_nothing()
    {
        _transfers.TakeFileAsync(Arg.Any<ElementReference>()).Returns((ChosenFile?)null);
        var tab = Render();

        await tab.Find("input.database-file").ChangeAsync(new ChangeEventArgs());

        Snackbar.ShownSnackbars.Should().BeEmpty();
        _dialogs.FindAll(".database-dialog").Should().BeEmpty();
    }

    [Fact]
    public async Task A_confirmed_restore_shows_the_upload_percent_then_the_replace_step_and_reloads_the_info()
    {
        var upload = new TaskCompletionSource<TransferResult>();
        Func<double, double, Task>? progress = null;
        _transfers.RestoreAsync(Arg.Do<Func<double, double, Task>>(p => progress = p)).Returns(upload.Task);
        var tab = Render();
        await OpenConfirmationAsync(tab);

        var confirm = _dialogs.FindAll(".mud-dialog-actions button")[1].ClickAsync(new MouseEventArgs());

        tab.WaitForAssertion(() => Overlay(tab).Should().Be("A restaurar a base de dados...|A enviar o ficheiro|Não feche nem atualize esta página."));
        _dialogs.WaitForAssertion(() => _dialogs.FindAll(".database-dialog").Should().BeEmpty());
        tab.Find(".database-percent").TextContent.Should().Be("0%");
        await tab.InvokeAsync(() => progress!(500_000, 1_960_437));
        Overlay(tab).Should().Be("A restaurar a base de dados...|A enviar o ficheiro (488.3 KB de 1.87 MB)|Não feche nem atualize esta página.");
        tab.Find(".database-percent").TextContent.Should().Be("26%");
        await tab.InvokeAsync(() => progress!(1_960_437, 1_960_437));
        Overlay(tab).Should().Be("A restaurar a base de dados...|A substituir os dados. Isto pode demorar alguns minutos.|Não feche nem atualize esta página.");
        tab.FindAll(".database-percent").Should().BeEmpty("indeterminate while the server replaces the data");

        upload.SetResult(new TransferResult(200, """{"backupCreatedAt":"2026-11-11T16:15:31.123Z","backupDatabase":"dib4t","collections":8,"documents":30754}"""));
        await confirm;

        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal(("Base de dados restaurada: 8 coleções, 30\u00A0754 documentos.", Severity.Success));
        await _api.Received(2).InfoAsync();
        tab.FindAll(".database-overlay").Should().BeEmpty();
    }

    [Theory]
    [InlineData(400, "O ficheiro não é um backup válido da Dib4t.", "O ficheiro não é um backup válido da Dib4t.")]
    [InlineData(409, "Já existe uma operação em curso (backup). Tente mais tarde.", "Já existe uma operação em curso (backup). Tente mais tarde.")]
    [InlineData(413, "request entity too large", "request entity too large")]
    [InlineData(500, "Erro ao restaurar o backup.", "Erro ao restaurar o backup.")]
    [InlineData(0, "", "Erro ao restaurar o backup")]
    public async Task A_restore_failure_shows_the_server_text_or_the_legacy_fallback(int status, string text, string shown)
    {
        _transfers.RestoreAsync(Arg.Any<Func<double, double, Task>>()).Returns(new TransferResult(status, text));
        var tab = Render();
        await OpenConfirmationAsync(tab);

        await _dialogs.FindAll(".mud-dialog-actions button")[1].ClickAsync(new MouseEventArgs());

        tab.WaitForAssertion(() => Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal((shown, Severity.Error)));
        await _api.Received(1).InfoAsync();
        tab.FindAll(".database-overlay").Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0, "A enviar o ficheiro (0 B de 0 B)", 0)]
    [InlineData(0, 100, "A enviar o ficheiro (0 B de 100 B)", 0)]
    [InlineData(5, 1000, "A enviar o ficheiro (5 B de 1000 B)", 1)]
    [InlineData(994, 1000, "A enviar o ficheiro (994 B de 1000 B)", 99)]
    [InlineData(995, 1000, DatabaseTab.ReplacingMessage, null)]
    public void Upload_progress_is_the_legacy_Math_round_percent(double loaded, double total, string message, int? percent) =>
        DatabaseTab.UploadProgress(loaded, total).Should().Be(new DatabaseTab.Operation(DatabaseTab.RestoreTitle, message, percent));

    private async Task OpenConfirmationAsync(IRenderedComponent<DatabaseTab> tab)
    {
        await tab.Find("input.database-file").ChangeAsync(new ChangeEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Find(".database-dialog").Should().NotBeNull());
    }

    private static string Overlay(IRenderedComponent<DatabaseTab> tab) =>
        string.Join("|", tab.FindAll(".database-overlay h2, .database-overlay p").Select(e => e.TextContent));

    private static string Stat(AngleSharp.Dom.IElement card) => string.Join("|",
        card.QuerySelector(".database-stat-label")!.TextContent, card.QuerySelector(".database-stat-value")!.TextContent,
        card.QuerySelector(".database-stat-detail")?.TextContent ?? string.Empty);
}
