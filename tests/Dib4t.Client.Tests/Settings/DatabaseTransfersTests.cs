using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Dib4t.Client.Tests.Settings;

/// <summary>
/// <see cref="DatabaseTransfers"/>: the browser does the transfer (<c>js/database.js</c>, faked by bUnit); here the
/// token, the URLs and what the HTTP handlers do for every other call (global error notification, "Invalid token.").
/// </summary>
public sealed class DatabaseTransfersTests : IAsyncDisposable
{
    private readonly ClientTestContext _context = new(storedToken: ClientTestContext.Token("Ana", "canSeeDefinicoes"));
    private readonly BunitJSModuleInterop _module;

    public DatabaseTransfersTests()
    {
        _module = _context.JSInterop.SetupModule(DatabaseTransfers.Module);
        _context.Services.AddScoped<DatabaseTransfers>();
    }

    private DatabaseTransfers Transfers => _context.Services.GetRequiredService<DatabaseTransfers>();

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task The_download_gets_the_backup_url_the_token_and_the_file_name()
    {
        _module.Setup<TransferResult>("downloadBackup", _ => true).SetResult(new TransferResult(200, string.Empty, 42));

        var result = await Transfers.DownloadBackupAsync("dib4t-backup-2026-07-22_10-05-07.dibbak", _ => Task.CompletedTask);

        result.Should().Be(new TransferResult(200, string.Empty, 42));
        var call = _module.Invocations.Single(i => i.Identifier == "downloadBackup");
        call.Arguments[0].Should().Be("http://localhost/api/database/backup");
        call.Arguments[1].Should().Be(ClientTestContext.Token("Ana", "canSeeDefinicoes"));
        call.Arguments[2].Should().Be("dib4t-backup-2026-07-22_10-05-07.dibbak");
        call.Arguments[3].Should().BeOfType<DotNetObjectReference<DatabaseTransfers.ProgressSink>>();
        Snackbar.ShownSnackbars.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task No_answer_or_a_5xx_shows_the_global_error_notification_as_for_every_request(int status)
    {
        _module.Setup<TransferResult>("restoreBackup", _ => true).SetResult(new TransferResult(status, "Erro ao restaurar o backup."));

        var result = await Transfers.RestoreAsync((_, _) => Task.CompletedTask);

        result.Status.Should().Be(status);
        Snackbar.ShownSnackbars.Select(s => (s.Message, s.Severity)).Should().Equal((ErrorNotificationHandler.UnexpectedError, Severity.Error));
        _module.Invocations.Single(i => i.Identifier == "restoreBackup").Arguments[0].Should().Be("http://localhost/api/database/restore");
    }

    [Theory]
    [InlineData(400, "O ficheiro não é um backup válido da Dib4t.")]
    [InlineData(409, "Já existe uma operação em curso (backup). Tente mais tarde.")]
    [InlineData(401, "Não tem permissão para concluir esta acção.")]
    public async Task A_4xx_is_left_to_the_page(int status, string text)
    {
        _module.Setup<TransferResult>("restoreBackup", _ => true).SetResult(new TransferResult(status, text));

        (await Transfers.RestoreAsync((_, _) => Task.CompletedTask)).Should().Be(new TransferResult(status, text));

        Snackbar.ShownSnackbars.Should().BeEmpty();
        _context.Invocations("removeToken").Should().Be(0);
    }

    [Fact]
    public async Task Invalid_token_logs_out_like_every_other_request()
    {
        _module.Setup<TransferResult>("downloadBackup", _ => true).SetResult(new TransferResult(400, "Invalid token."));

        await Transfers.DownloadBackupAsync("x.dibbak", _ => Task.CompletedTask);

        _context.Invocations("removeToken").Should().Be(1);
        _context.Tokens.Current.Should().BeNull();
    }

    [Fact]
    public async Task A_script_failure_is_no_answer()
    {
        _module.Setup<TransferResult>("downloadBackup", _ => true).SetException(new JSException("boom"));

        var result = await Transfers.DownloadBackupAsync("x.dibbak", _ => Task.CompletedTask);

        result.Should().Be(new TransferResult(0, string.Empty));
        Snackbar.ShownSnackbars.Should().ContainSingle();
    }

    [Fact]
    public async Task The_progress_callbacks_reach_the_page()
    {
        var download = new List<double>();
        var upload = new List<(double, double)>();
        var sink = new DatabaseTransfers.ProgressSink
        {
            Download = loaded => { download.Add(loaded); return Task.CompletedTask; },
            Upload = (loaded, total) => { upload.Add((loaded, total)); return Task.CompletedTask; },
        };

        await sink.OnDownloadProgress(10);
        await sink.OnUploadProgress(5, 20);
        await new DatabaseTransfers.ProgressSink().OnDownloadProgress(1);

        download.Should().Equal(10);
        upload.Should().Equal((5d, 20d));
    }

    [Fact]
    public async Task The_file_chooser_calls_reach_the_module()
    {
        _module.Setup<ChosenFile?>("takeFile", _ => true).SetResult(new ChosenFile("a.dibbak", 1, 2));
        _module.SetupVoid("chooseFile", _ => true).SetVoidResult();
        _module.SetupVoid("forgetFile").SetVoidResult();
        _module.SetupVoid("guardUnload", _ => true).SetVoidResult();
        var input = new ElementReference("input");

        await Transfers.ChooseAsync(input);
        (await Transfers.TakeFileAsync(input)).Should().Be(new ChosenFile("a.dibbak", 1, 2));
        await Transfers.ForgetFileAsync();
        await Transfers.GuardUnloadAsync(true);

        _module.Invocations.Select(i => i.Identifier).Should().Equal("chooseFile", "takeFile", "forgetFile", "guardUnload");
        _module.Invocations.Last().Arguments.Should().Equal(true);
    }
}
