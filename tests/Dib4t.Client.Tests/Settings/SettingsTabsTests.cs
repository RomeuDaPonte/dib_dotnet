using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Components.Settings;
using Dib4t.Contracts.Account;
using Dib4t.Contracts.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Settings;

/// <summary>The settings tabs (legacy <c>components/settings/*</c>) against substituted API clients.</summary>
public sealed class SettingsTabsTests : IAsyncDisposable
{
    private const string BrunoId = "5f1e9a2b3c4d5e6f70819204";

    private readonly ClientTestContext _context = new();
    private readonly IUsersApi _users = Substitute.For<IUsersApi>();
    private readonly IPricesApi _prices = Substitute.For<IPricesApi>();
    private readonly IPaymentMethodsApi _methods = Substitute.For<IPaymentMethodsApi>();
    private readonly IPrinterSettingsApi _printers = Substitute.For<IPrinterSettingsApi>();

    public SettingsTabsTests()
    {
        _context.Services.AddSingleton(_users);
        _context.Services.AddSingleton(_prices);
        _context.Services.AddSingleton(_methods);
        _context.Services.AddSingleton(_printers);
        _users.ListAsync().Returns(ApiResult.Success<IReadOnlyList<UserListItemResponse>>(
        [
            new(["canSeeOrcamentos", null, "canSeeDefinicoes"], "Desenvolvimento", "5f1e9a2b3c4d5e6f70819203", "Ana Administradora", "ana.admin@dib4t.local"),
            new(["canSeeObras", "canSeeLeituras"], "Maquinação", BrunoId, "Bruno Maquinação", "bruno.limitado@dib4t.local"),
        ]));
        _prices.GetAsync().Returns(ApiResult.Success(new PricesResponse(10, 10, 10, 10, 10, 35)));
        _methods.ListAsync().Returns(ApiResult.Success<IReadOnlyList<PaymentMethodResponse>>([new("5f1e9a2b3c4d5e6f70819301", "Pronto pagamento", 0)]));
        _printers.ListAsync().Returns(ApiResult.Success<IReadOnlyList<PrinterSettingsResponse>>([new("5f1e9a2b3c4d5e6f70819401", "127.0.0.1", 9100, "producao", 0)]));
    }

    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Users_are_listed_and_filtered_like_the_legacy_table()
    {
        var tab = Render<UsersTab>();

        tab.FindAll("tbody tr").Select(r => r.QuerySelector("td")!.TextContent).Should().Equal("Ana Administradora", "Bruno Maquinação");
        await tab.Find("#users-filter").InputAsync(new ChangeEventArgs { Value = "BRUNO" });
        tab.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("bruno.limitado@dib4t.local");
        await tab.Find("#users-filter").InputAsync(new ChangeEventArgs { Value = "ANA.ADMIN" });
        tab.FindAll("tbody tr td").Should().BeEmpty("the e-mail filter is case-sensitive, as legacy");
        tab.Markup.Should().Contain("Tabela densa").And.Contain("Nome do utilizador").And.Contain("Opções");
    }

    [Fact]
    public async Task Editing_a_user_sends_the_legacy_body_and_updates_the_row()
    {
        _users.UpdateAsync(BrunoId, Arg.Any<UserRequest>()).Returns(ApiResult.Success(
            new UserResponse(BrunoId, "Bruno Silva", "bruno.limitado@dib4t.local", ["canSeeOrcamentos", "canSeeObras", null, null, null, null, "canSeeLeituras"])));
        var tab = Render<UsersTab>();

        await tab.Find("button[aria-label='Editar Bruno Maquinação']").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Editar user: Bruno Maquinação"));
        await _dialogs.Find("#user-name").InputAsync(new ChangeEventArgs { Value = "Bruno Silva" });
        await _dialogs.FindAll("input[type='checkbox']")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await _dialogs.Find("#user-form").SubmitAsync();

        await _users.Received(1).UpdateAsync(BrunoId, Arg.Is<UserRequest>(r => r.Name == "Bruno Silva" && r.Password == string.Empty
            && r.FieldOfCompetencies == "Maquinação"
            && r.Roles.SequenceEqual(new[] { "canSeeOrcamentos", "canSeeObras", null, null, null, null, "canSeeLeituras" })));
        tab.WaitForAssertion(() => tab.FindAll("tbody tr td").Select(td => td.TextContent).Should().Contain("Bruno Silva"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Update bem sucedido!" && s.Severity == Severity.Success);
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("#user-form").Should().BeEmpty("the dialog closes after a save"));
    }

    [Fact]
    public async Task A_new_user_is_checked_before_sending_and_the_server_text_is_shown()
    {
        _users.CreateAsync(Arg.Any<UserRequest>()).Returns(ApiResult.Failure<UserResponse>(HttpStatusCode.BadRequest, "Email já registado."));
        var tab = Render<UsersTab>();

        await tab.FindAll("button").First(b => b.TextContent.Trim() == "Add User").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Novo user"));
        await _dialogs.Find("#user-form").SubmitAsync();
        _dialogs.Markup.Should().Contain("name is a required field").And.Contain("password is a required field");
        await _users.DidNotReceiveWithAnyArgs().CreateAsync(default!);

        await _dialogs.Find("#user-name").InputAsync(new ChangeEventArgs { Value = "Ana Nova" });
        await _dialogs.Find("#user-email").InputAsync(new ChangeEventArgs { Value = "ana.admin@dib4t.local" });
        await _dialogs.Find("#user-password").InputAsync(new ChangeEventArgs { Value = "char-test-1" });
        await _dialogs.Find("#user-confirm-password").InputAsync(new ChangeEventArgs { Value = "char-test-1" });
        await _dialogs.Find("#user-form").SubmitAsync();

        await _users.Received(1).CreateAsync(Arg.Is<UserRequest>(r => r.Roles.Count == 7 && r.Roles.All(role => role == null) && r.FieldOfCompetencies == "Desenvolvimento"));
        tab.WaitForAssertion(() => Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Email já registado." && s.Severity == Severity.Error));
        _dialogs.FindAll("#user-form").Should().ContainSingle("a refused save keeps the dialog open, as legacy");
    }

    [Fact]
    public async Task Prices_show_in_the_legacy_units_and_are_sent_as_typed()
    {
        _prices.UpdateAsync(Arg.Any<PricesRequest>()).Returns(ApiResult.Success(new PricesResponse(32.5, 10, 10, 10, 10, 35)));
        var tab = Render<PricesTab>();

        tab.FindAll("tbody td").Take(2).Select(td => td.TextContent).Should().Equal("35 %", "10 €");
        await tab.Find("button[aria-label='Editar preços']").ClickAsync(new MouseEventArgs());
        tab.WaitForAssertion(() => _dialogs.Find("#prices-automation"));
        await _dialogs.Find("#prices-automation").InputAsync(new ChangeEventArgs { Value = "abc" });
        _dialogs.Markup.Should().Contain("automation must be a `number` type");
        await _dialogs.Find("#prices-automation").InputAsync(new ChangeEventArgs { Value = "32.5" });
        await _dialogs.Find("#prices-form").SubmitAsync();

        await _prices.Received(1).UpdateAsync(new PricesRequest("32.5", "10", "10", "10", "35", "10"));
        tab.WaitForAssertion(() => tab.FindAll("tbody td")[1].TextContent.Should().Be("32.5 €"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Preços atualizados com sucesso!");
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("#prices-form").Should().BeEmpty("the dialog closes after a save"));
    }

    [Fact]
    public async Task Payment_methods_are_added_on_enter_and_deleted()
    {
        _methods.AddAsync("Cheque").Returns(ApiResult.Success(new PaymentMethodResponse("5f1e9a2b3c4d5e6f70819302", "Cheque", 0)));
        _methods.AddAsync("Pronto pagamento").Returns(ApiResult.Failure<PaymentMethodResponse>(HttpStatusCode.BadRequest, "Modo de pagamento com esse nome já está registado"));
        _methods.DeleteAsync("5f1e9a2b3c4d5e6f70819301").Returns(ApiResult.Success(new PaymentMethodResponse("5f1e9a2b3c4d5e6f70819301", "Pronto pagamento", 0)));
        var tab = Render<PaymentMethodsTab>();

        await tab.Find("#payment-method-new").InputAsync(new ChangeEventArgs { Value = "Cheque" });
        await tab.Find("#payment-method-new").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        tab.WaitForAssertion(() => tab.Markup.Should().Contain("Cheque"));
        await tab.Find("#payment-method-new").InputAsync(new ChangeEventArgs { Value = "Pronto pagamento" });
        await tab.Find("#payment-method-new").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await tab.Find("button[aria-label='Apagar Pronto pagamento']").ClickAsync(new MouseEventArgs());

        tab.WaitForAssertion(() => tab.Markup.Should().NotContain("Apagar Pronto pagamento"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Modo de pagamento com esse nome já está registado");
    }

    [Fact]
    public async Task Printers_are_listed_edited_and_saved_with_the_legacy_texts()
    {
        _printers.UpdateAsync("5f1e9a2b3c4d5e6f70819401", Arg.Any<PrinterSettingsRequest>())
            .Returns(ApiResult.Success(new PrinterSettingsResponse("5f1e9a2b3c4d5e6f70819401", "127.0.0.11", 9101, "producao", 0)));
        var tab = Render<PrintersTab>();

        tab.Markup.Should().Contain("127.0.0.1:9100").And.Contain("Adicionar Impressora").And.Contain("Guardar");
        await tab.Find("button[aria-label='Editar 127.0.0.1']").ClickAsync(new MouseEventArgs());
        tab.Markup.Should().Contain("Editar Impressora").And.Contain("Cancelar Edição").And.Contain("Atualizar");
        await tab.Find("#printer-ip").InputAsync(new ChangeEventArgs { Value = "127.0.0.11" });
        await tab.Find("#printer-port").InputAsync(new ChangeEventArgs { Value = "9101" });
        await tab.Find("form").SubmitAsync();

        await _printers.Received(1).UpdateAsync("5f1e9a2b3c4d5e6f70819401", new PrinterSettingsRequest("127.0.0.11", 9101, "producao"));
        tab.WaitForAssertion(() => tab.Markup.Should().Contain("127.0.0.11:9101").And.Contain("Adicionar Impressora"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Impressora atualizada com sucesso!");
    }

    [Fact]
    public void Info_shows_the_new_version()
    {
        var tab = Render<InfoTab>();

        tab.FindAll(".settings-info-line").Select(p => p.TextContent).Should().Equal(
            "Webapp ERP", "Software ID: 69988ec8-c573-4799-bafc-0b6d30340961", "Versão: 1.0.0",
            "Este software foi totalmente desenvolvido pela DIB4T,LDA para uso interno.", "2018, DIB4T");
    }

    [Fact]
    public async Task The_tab_bar_shows_the_legacy_tabs_plus_Registos_and_switches_by_click_and_arrow_keys()
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana", "canSeeDefinicoes"));
        await _context.Session.GetAuthenticationStateAsync();
        var page = Render<Dib4t.Client.Pages.Landing.Settings>();

        page.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().Equal("Utilizadores", "Preços", "M/Pagamento", "Impressoras", "Registos", "Info");
        page.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be("Utilizadores");
        await page.FindAll("[role=tab]")[5].ClickAsync(new MouseEventArgs());
        page.Find("[role=tabpanel]").TextContent.Should().Contain("Versão: 1.0.0");
        await page.Find("[role=tab][aria-selected=true]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        page.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be("Utilizadores", "the arrow keys wrap around");
        page.Find("[role=tabpanel]").GetAttribute("aria-labelledby").Should().Be(page.Find("[role=tab][aria-selected=true]").Id);
    }

    [Fact]
    public async Task The_page_needs_canSeeDefinicoes()
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Bruno", "canSeeObras"));
        await _context.Session.GetAuthenticationStateAsync();

        var page = Render<Dib4t.Client.Pages.Landing.Settings>();

        page.Markup.Should().Contain("Não tem permissão para concluir esta acção.").And.NotContain("Utilizadores");
    }

    private IRenderedComponent<TComponent> Render<TComponent>()
        where TComponent : IComponent
    {
        _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return _context.Render<TComponent>();
    }
}
