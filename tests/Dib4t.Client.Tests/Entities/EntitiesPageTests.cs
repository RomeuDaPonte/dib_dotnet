using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Components.Entities;
using Dib4t.Client.Entities;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Entities;

/// <summary>The entities page (legacy <c>components/entities/*</c>) against substituted API clients.</summary>
public sealed class EntitiesPageTests : IAsyncDisposable
{
    private const string AcosId = "5f1e9a2b3c4d5e6f70819501";

    private readonly ClientTestContext _context = new();
    private readonly IEntityApi _entities = Substitute.For<IEntityApi>();
    private readonly IPaymentMethodsApi _methods = Substitute.For<IPaymentMethodsApi>();
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    public EntitiesPageTests()
    {
        _context.Services.AddSingleton(_entities);
        _context.Services.AddSingleton(_methods);
        _entities.ListAsync().Returns(ApiResult.Success<IReadOnlyList<EntityResponse>>(
        [
            Entity(AcosId, "Aços Especiais", "Porto", "Cliente"),
            Entity("5f1e9a2b3c4d5e6f70819502", "Cortiças", "Santa Maria da Feira", "Cliente e Fornecedor"),
            Entity("5f1e9a2b3c4d5e6f70819503", "metalúrgica", "Lisboa", "Fornecedor") with { FiscalNumber = null, Address = null },
        ]));
        _methods.ListAsync().Returns(ApiResult.Success<IReadOnlyList<PaymentMethodResponse>>(
            [new("5f1e9a2b3c4d5e6f70819301", "Pronto pagamento", 0), new("5f1e9a2b3c4d5e6f70819302", "30 dias", 0)]));
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Entities_are_sorted_ordinally_filtered_like_legacy_and_paged_by_25()
    {
        var table = Render<EntitiesTable>();

        Names(table).Should().Equal("Aços Especiais", "Cortiças", "metalúrgica");
        table.Markup.Should().Contain("1-3 of 3").And.Contain("Tabela densa").And.Contain("Localidade").And.Contain("Opções");
        table.Find("#entities-rows-per-page").GetAttribute("value").Should().Be("25");

        await table.Find("#entities-filter").InputAsync(new ChangeEventArgs { Value = "METAL" });
        Names(table).Should().Equal("metalúrgica");
        await table.Find("#entities-filter").InputAsync(new ChangeEventArgs { Value = "porto" });
        table.FindAll("tbody tr td").Should().BeEmpty("the location filter is case-sensitive, as legacy");
        await table.Find("#entities-filter").InputAsync(new ChangeEventArgs { Value = "Fornecedor" });
        Names(table).Should().Equal("Cortiças", "metalúrgica");
    }

    [Fact]
    public void The_sort_keys_order_like_javascript_strings()
    {
        new[] { "b", "B", "á", "a", null }.OrderBy(EntityList.SortKey).Should().Equal(null, "B", "a", "b", "á");
    }

    [Fact]
    public async Task Editing_sends_the_legacy_body_without_the_values_the_entity_lacks_and_updates_the_row()
    {
        _entities.UpdateAsync("5f1e9a2b3c4d5e6f70819503", Arg.Any<EntityRequest>()).Returns(ApiResult.Success(
            Entity("5f1e9a2b3c4d5e6f70819503", "Metalúrgica Sul", "Lisboa", "Fornecedor")));
        var table = Render<EntitiesTable>();

        await table.Find("button[aria-label='Editar metalúrgica']").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Editar entidade: metalúrgica"));
        await _dialogs.Find("#entity-name").InputAsync(new ChangeEventArgs { Value = "Metalúrgica Sul" });
        await _dialogs.Find("#entity-form").SubmitAsync();

        await _entities.Received(1).UpdateAsync("5f1e9a2b3c4d5e6f70819503",
            new EntityRequest("Metalúrgica Sul", null, null, "4000-001", "Lisboa", "30 dias", "Fornecedor"));
        table.WaitForAssertion(() => Names(table).Should().Equal("Aços Especiais", "Cortiças", "Metalúrgica Sul"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Update bem sucedido!" && s.Severity == Severity.Success);
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("#entity-form").Should().BeEmpty("the dialog closes after a save"));
    }

    [Fact]
    public async Task A_new_entity_is_checked_before_sending_and_the_server_text_is_shown()
    {
        _entities.CreateAsync(Arg.Any<EntityRequest>()).Returns(ApiResult.Failure<EntityResponse>(HttpStatusCode.BadRequest, "Nome de entidade já registado"));
        var table = Render<EntitiesTable>();

        await table.FindAll("button").First(b => b.TextContent.Trim() == "Add Entidade").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Nova entidade"));
        await _dialogs.Find("#entity-form").SubmitAsync();
        _dialogs.Markup.Should().Contain("name is a required field").And.Contain("type is a required field").And.Contain("paymentCondition is a required field");
        await _entities.DidNotReceiveWithAnyArgs().CreateAsync(default!);

        await _dialogs.Find("#entity-name").InputAsync(new ChangeEventArgs { Value = "Cortiças" });
        await _dialogs.Find("#entity-type").ChangeAsync(new ChangeEventArgs { Value = "Cliente" });
        _dialogs.Markup.Should().NotContain("type is a required field", "validated on every change after the first submit");
        await _dialogs.Find("#entity-payment-condition").ChangeAsync(new ChangeEventArgs { Value = "Pronto pagamento" });
        await _dialogs.Find("#entity-form").SubmitAsync();

        await _entities.Received(1).CreateAsync(new EntityRequest("Cortiças", "", "", "", "", "Pronto pagamento", "Cliente"));
        table.WaitForAssertion(() => Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Nome de entidade já registado" && s.Severity == Severity.Error));
        _dialogs.FindAll("#entity-form").Should().ContainSingle("a refused save keeps the dialog open, as legacy");
    }

    [Fact]
    public async Task A_created_entity_joins_the_whole_list_and_the_success_text_is_the_legacy_one()
    {
        _entities.CreateAsync(Arg.Any<EntityRequest>()).Returns(ApiResult.Success(Entity("5f1e9a2b3c4d5e6f70819509", "Nova Lda.", "Braga", "Cliente")));
        var table = Render<EntitiesTable>();
        await table.Find("#entities-filter").InputAsync(new ChangeEventArgs { Value = "Aços" });

        await table.FindAll("button").First(b => b.TextContent.Trim() == "Add Entidade").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Find("#entity-name"));
        await _dialogs.Find("#entity-name").InputAsync(new ChangeEventArgs { Value = "Nova Lda." });
        await _dialogs.Find("#entity-type").ChangeAsync(new ChangeEventArgs { Value = "Cliente" });
        await _dialogs.Find("#entity-payment-condition").ChangeAsync(new ChangeEventArgs { Value = "30 dias" });
        await _dialogs.Find("#entity-form").SubmitAsync();
        table.WaitForAssertion(() => Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Entidade guardada com sucesso!"));
        await table.Find("#entities-filter").InputAsync(new ChangeEventArgs { Value = "" });

        Names(table).Should().Equal("Aços Especiais", "Cortiças", "Nova Lda.", "metalúrgica");
    }

    [Fact]
    public async Task A_payment_condition_no_longer_in_the_list_is_kept()
    {
        _entities.ListAsync().Returns(ApiResult.Success<IReadOnlyList<EntityResponse>>([Entity(AcosId, "Aços", "Porto", "Cliente") with { PaymentCondition = "Antiga" }]));
        var table = Render<EntitiesTable>();

        await table.Find("button[aria-label='Editar Aços']").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Find("#entity-payment-condition"));

        _dialogs.FindAll("#entity-payment-condition option").Select(o => o.TextContent).Should().Equal("Antiga", "Pronto pagamento", "30 dias");
    }

    [Fact]
    public async Task The_page_needs_canSeeEntidades()
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Bruno", "canSeeObras"));
        await _context.Session.GetAuthenticationStateAsync();

        var page = Render<Dib4t.Client.Pages.Landing.Entities>();

        page.Markup.Should().Contain("Não tem permissão para concluir esta acção.").And.NotContain("Add Entidade");
        await _entities.DidNotReceive().ListAsync();
    }

    private static EntityResponse Entity(string id, string name, string location, string type) =>
        new(id, name, "500100200", "Rua", "4000-001", location, "30 dias", type, 0);

    private static IEnumerable<string> Names(IRenderedComponent<EntitiesTable> table) =>
        table.FindAll("tbody tr").Select(r => r.QuerySelector("td")!.TextContent);

    private IRenderedComponent<TComponent> Render<TComponent>()
        where TComponent : IComponent
    {
        _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return _context.Render<TComponent>();
    }
}
