using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Budgets;

/// <summary>
/// The budget details page (legacy <c>components/budgetDetails/*</c>) against substituted API clients: the header's
/// live totals and margin color, what each change sends and when (the total at once, the task on blur, the header
/// inputs on blur), the lock and its warnings, the description and "Concluir", the import (D24) and the fixed
/// legacy UI defects (header totals from the rows' current values, a budget without tasks can get one).
/// </summary>
public sealed class BudgetDetailsPageTests : IAsyncDisposable
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810001";
    private const string UserId = "5f1e9a2b3c4d5e6f70819203";

    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    private static readonly EntityResponse Metal = new("5f1e9a2b3c4d5e6f70820001", "Metalúrgica do Ave", null, null, null, null, "30 dias", "Cliente", 0);

    private readonly ClientTestContext _context = new();
    private readonly IBudgetApi _budgets = Substitute.For<IBudgetApi>();
    private readonly ITaskApi _tasks = Substitute.For<ITaskApi>();
    private readonly IPricesApi _prices = Substitute.For<IPricesApi>();
    private readonly IPaymentMethodsApi _paymentMethods = Substitute.For<IPaymentMethodsApi>();
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    public BudgetDetailsPageTests()
    {
        _context.Services.AddSingleton(_budgets);
        _context.Services.AddSingleton(_tasks);
        _context.Services.AddSingleton(_prices);
        _context.Services.AddSingleton(_paymentMethods);
        var users = Substitute.For<IUsersApi>();
        users.ListAsync().Returns(ApiResult.Failure<IReadOnlyList<Dib4t.Contracts.Account.UserListItemResponse>>(HttpStatusCode.Unauthorized, "Não tem permissão para concluir esta acção."));
        _context.Services.AddSingleton(users);
        _context.Services.AddTransient<BudgetDetailsState>();
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", "canSeeOrcamentos"));
        _budgets.GetAsync(BudgetId).Returns(ApiResult.Success(Budget()));
        _budgets.ListClientsAsync().Returns(ApiResult.Success<IReadOnlyList<EntityResponse>>([Metal]));
        _budgets.PatchAsync(BudgetId, Arg.Any<PatchBudgetRequest>()).Returns(call => ApiResult.Success(Patched(call.Arg<PatchBudgetRequest>())));
        _tasks.ListAsync(BudgetId).Returns(ApiResult.Success<IReadOnlyList<BudgetTaskResponse>>([Task("t1", "development", 30, 20), Task("t2", "machining", 38.5, 15)]));
        _tasks.ListTypesAsync().Returns(ApiResult.Success<IReadOnlyList<string>>(["automation", "consultancy", "development", "machining", "assembly", "others", "components"]));
        _tasks.UpdateAsync(Arg.Any<string>(), BudgetId, Arg.Any<UpdateTaskRequest>()).Returns(call =>
        {
            var request = call.Arg<UpdateTaskRequest>();
            return ApiResult.Success(Task(call.ArgAt<string>(0), request.TaskType!, Number(request.UnitPrice), Number(request.Quantity), request.Description));
        });
        _prices.GetAsync().Returns(ApiResult.Success(new PricesResponse(32.5, 11, 12, 13, 14, 35)));
        _paymentMethods.ListAsync().Returns(ApiResult.Success<IReadOnlyList<PaymentMethodResponse>>([new("p1", "30 dias", 0), new("p2", "Pronto pagamento", 0)]));
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task The_header_shows_the_live_totals_and_the_effective_margin_in_its_color()
    {
        var page = await RenderAsync();

        // 20 × 30 + 15 × 38.5 = 1177.5; with 25 %: 1471.88; the saved total 1471 gives 24.93 % (yellow; Node: 24.93).
        page.Find(".budget-tasks-total").TextContent.Should().Be("1177.50");
        page.Find(".budget-with-margin").TextContent.Should().Be("1471.88");
        page.Find(".budget-effective-margin").TextContent.Should().Be("24.93 %");
        page.Find(".budget-effective-margin").GetAttribute("style").Should().Contain("color: yellow");
        page.Find("#budget-margin").GetAttribute("value").Should().Be("25");
        page.Find("#budget-total").GetAttribute("value").Should().Be("1471");
        page.Find("#budget-payment").GetAttribute("value").Should().Be("30 dias");
        page.FindAll(".budget-task").Should().HaveCount(2);
        page.FindAll(".budget-task-total").Select(p => p.TextContent).Should().Equal("600 €", "577.5 €");
        page.Find("#task-t1-type option[value='development']").TextContent.Should().Be("Desenvolvimento");
        page.FindAll("button[aria-label='Apagar']").Should().HaveCount(2, "with more than one task");
        page.Find("button[aria-label='Bloquear']").Should().NotBeNull("an incomplete budget is editable");
        page.Find("button[aria-label='Importar']").Should().NotBeNull();
    }

    [Fact]
    public async Task A_quantity_change_saves_the_total_at_once_and_the_task_on_blur()
    {
        var page = await RenderAsync();

        await page.Find("#task-t1-quantity").InputAsync(new ChangeEventArgs { Value = "10" });

        // 10 × 30 + 577.5 = 877.5 → total 877.5 + trunc(877.5 × 25 / 100) = 877.5 + 219 = 1096.5
        await _budgets.Received(1).PatchAsync(BudgetId, new PatchBudgetRequest("total", 1096.5));
        page.WaitForAssertion(() => page.Find(".budget-tasks-total").TextContent.Should().Be("877.50"));
        page.Find("#budget-total").GetAttribute("value").Should().Be("1096.5", "the saved budget replaces the inputs");
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!);

        await page.Find("#task-t1-quantity").BlurAsync(new FocusEventArgs());

        await _tasks.Received(1).UpdateAsync("t1", BudgetId, new UpdateTaskRequest("development", "x", "10", 30d));
    }

    [Fact]
    public async Task A_type_change_sets_the_types_price_others_and_components_cost_1()
    {
        var page = await RenderAsync();

        await page.Find("#task-t2-type").ChangeAsync(new ChangeEventArgs { Value = "assembly" });
        await page.Find("#task-t1-type").ChangeAsync(new ChangeEventArgs { Value = "others" });

        page.WaitForAssertion(() => page.FindAll(".budget-task-total").Select(p => p.TextContent).Should().Equal("20 €", "210 €"));
        await page.Find("#task-t1-type").BlurAsync(new FocusEventArgs());
        await _tasks.Received(1).UpdateAsync("t1", BudgetId, new UpdateTaskRequest("others", "x", 20d, 1d));
    }

    [Fact]
    public async Task A_margin_change_sets_the_total_and_the_blur_saves_the_margin_then_the_total()
    {
        var page = await RenderAsync();

        await page.Find("#budget-margin").InputAsync(new ChangeEventArgs { Value = "30" });

        page.Find("#budget-total").GetAttribute("value").Should().Be("1530.75", "1177.5 + 1177.5 × 30 / 100, toFixed(2)");
        await _budgets.DidNotReceiveWithAnyArgs().PatchAsync(default!, default!);

        await page.Find("#budget-margin").BlurAsync(new FocusEventArgs());

        Received.InOrder(() =>
        {
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("margin", "30"));
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("total", "1530.75"));
        });
    }

    [Fact]
    public async Task Days_validity_and_payment_are_saved_on_blur_with_what_the_input_holds()
    {
        var page = await RenderAsync();

        await page.Find("#budget-days").BlurAsync(new FocusEventArgs());
        await page.Find("#budget-validity").InputAsync(new ChangeEventArgs { Value = "" });
        await page.Find("#budget-validity").BlurAsync(new FocusEventArgs());
        await page.Find("#budget-payment").ChangeAsync(new ChangeEventArgs { Value = "Pronto pagamento" });
        await page.Find("#budget-payment").BlurAsync(new FocusEventArgs());

        Received.InOrder(() =>
        {
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("daysNeededToDoTheWork", 45d)); // unchanged: the stored number
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("validity", ""));
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("paymentMethod", "Pronto pagamento"));
        });
    }

    [Fact]
    public async Task A_locked_budget_refuses_every_change_with_the_legacy_warnings_until_unlocked()
    {
        _budgets.GetAsync(BudgetId).Returns(ApiResult.Success(Budget() with { Complete = true, WasPdfDownloaded = true }));
        var page = await RenderAsync();

        page.Find("#budget-margin").HasAttribute("readonly").Should().BeTrue();
        page.FindAll("button[aria-label='Importar']").Should().BeEmpty("import only while editable");
        await page.Find("#budget-margin").KeyDownAsync(new KeyboardEventArgs { Key = "5" });
        await page.Find("#budget-margin").BlurAsync(new FocusEventArgs());
        await page.Find("#task-t1-type").ChangeAsync(new ChangeEventArgs { Value = "others" });
        await page.Find("#budget-payment").ChangeAsync(new ChangeEventArgs { Value = "Pronto pagamento" });
        await page.FindAll("button[aria-label='Adicionar']")[0].ClickAsync(new MouseEventArgs());
        await page.FindAll("button[aria-label='Apagar']")[0].ClickAsync(new MouseEventArgs());

        // (the snackbar shows a repeated warning once while it is visible; legacy stacks one per attempt)
        Snackbar.ShownSnackbars.Select(s => s.Message).Distinct().Should().BeEquivalentTo("Oçamento bloqueado", "Não pode editar o orçamento");
        await _budgets.DidNotReceiveWithAnyArgs().PatchAsync(default!, default!);
        await _tasks.DidNotReceiveWithAnyArgs().CreateDefaultAsync(default!);
        page.Find("#budget-payment").GetAttribute("value").Should().Be("30 dias");

        await page.Find("button[aria-label='Desbloquear']").ClickAsync(new MouseEventArgs());
        page.Find("#budget-margin").HasAttribute("readonly").Should().BeFalse();
        await page.Find("#budget-margin").BlurAsync(new FocusEventArgs());
        await _budgets.Received().PatchAsync(BudgetId, new PatchBudgetRequest("margin", 25d));
    }

    [Fact]
    public async Task A_save_that_resets_the_flags_unlocks_a_budget_that_was_locked()
    {
        _budgets.GetAsync(BudgetId).Returns(ApiResult.Success(Budget() with { Complete = true, WasPdfDownloaded = true }));
        var page = await RenderAsync();
        await page.Find("button[aria-label='Desbloquear']").ClickAsync(new MouseEventArgs());
        await page.Find("button[aria-label='Bloquear']").ClickAsync(new MouseEventArgs());
        await page.Find("button[aria-label='Desbloquear']").ClickAsync(new MouseEventArgs());

        await page.Find("#budget-total").BlurAsync(new FocusEventArgs());

        page.WaitForAssertion(() => page.Find("button[aria-label='Bloquear']").Should().NotBeNull());
        page.Find("#budget-total").HasAttribute("readonly").Should().BeFalse("the saved flags are false: editable");
    }

    [Fact]
    public async Task The_description_is_saved_on_blur_and_concluir_completes_the_budget()
    {
        var page = await RenderAsync();
        await page.Find("button[aria-label='Alternar']").ClickAsync(new MouseEventArgs());
        page.FindAll(".budget-task").Should().BeEmpty();

        await page.Find("#budget-description").InputAsync(new ChangeEventArgs { Value = "Texto novo" });
        await page.Find("#budget-description").BlurAsync(new FocusEventArgs());
        await page.Find("button.budget-complete").ClickAsync(new MouseEventArgs());

        Received.InOrder(() =>
        {
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("description", "Texto novo"));
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("complete", true));
        });
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Orçamento completo" && s.Severity == Severity.Success);
    }

    [Fact]
    public async Task Add_and_remove_save_the_new_total_and_the_last_task_cannot_be_removed()
    {
        _tasks.CreateDefaultAsync(BudgetId).Returns(ApiResult.Success(Task("t3", "automation", 32.5, 1, string.Empty)));
        _tasks.DeleteAsync("t1", BudgetId).Returns(ApiResult.Success(Task("t1", "development", 30, 20)));
        _tasks.DeleteAsync("t2", BudgetId).Returns(ApiResult.Success(Task("t2", "machining", 38.5, 15)));
        var page = await RenderAsync();

        await page.FindAll("button[aria-label='Adicionar']")[0].ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".budget-task").Should().HaveCount(3));
        await page.FindAll("button[aria-label='Apagar']")[0].ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".budget-task").Should().HaveCount(2));
        await page.FindAll("button[aria-label='Apagar']")[0].ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll(".budget-task").Should().HaveCount(1));
        page.FindAll("button[aria-label='Apagar']").Should().BeEmpty();
        Received.InOrder(() =>
        {
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("total", 1210d + 302)); // 1210 + trunc(1210 × 0.25)
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("total", 610d + 152));
            _budgets.PatchAsync(BudgetId, new PatchBudgetRequest("total", 32.5 + 8));
        });
    }

    [Fact]
    public async Task A_server_text_is_shown_as_an_error()
    {
        _tasks.UpdateAsync("t1", BudgetId, Arg.Any<UpdateTaskRequest>())
            .Returns(ApiResult.Failure<BudgetTaskResponse>(HttpStatusCode.BadRequest, "\"quantity\" must be a number"));
        var page = await RenderAsync();

        await page.Find("#task-t1-quantity").InputAsync(new ChangeEventArgs { Value = "" });
        await page.Find("#task-t1-quantity").BlurAsync(new FocusEventArgs());

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "\"quantity\" must be a number" && s.Severity == Severity.Error);
        await _tasks.Received(1).UpdateAsync("t1", BudgetId, new UpdateTaskRequest("development", "x", string.Empty, 30d));
    }

    [Fact]
    public async Task D24_an_import_sends_this_budget_into_another_number_and_this_page_does_not_change()
    {
        var page = await RenderAsync();
        _budgets.ImportAsync(BudgetId, Arg.Any<ImportBudgetRequest>()).Returns(ApiResult.Success(Budget() with { Id = "other", Margin = 32.5, Total = 1234 }));

        await page.Find("button[aria-label='Importar']").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Nada é apagado."));
        await _dialogs.Find("#import-number").InputAsync(new ChangeEventArgs { Value = "26-0003" });
        await _dialogs.Find("#import-form").SubmitAsync();

        await _budgets.Received(1).ImportAsync(BudgetId, new ImportBudgetRequest(true, true, "26-0003"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Importado com sucesso");
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().NotContain("Nª Orçamento"));
        page.FindAll(".budget-task").Should().HaveCount(2, "this budget's tasks are unchanged");
        page.Find("#budget-margin").GetAttribute("value").Should().Be("25", "the answer is the receiving budget, not this one");
        await _tasks.Received(1).ListAsync(BudgetId); // only the first load
    }

    [Fact]
    public async Task A_budget_without_tasks_can_get_one()
    {
        _tasks.ListAsync(BudgetId).Returns(ApiResult.Success<IReadOnlyList<BudgetTaskResponse>>([]));
        _tasks.CreateDefaultAsync(BudgetId).Returns(ApiResult.Success(Task("t3", "automation", 32.5, 1, string.Empty)));
        var page = await RenderAsync();

        await page.Find(".budget-tasks button").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll(".budget-task").Should().HaveCount(1));
        await _tasks.Received(1).CreateDefaultAsync(BudgetId);
    }

    private async Task<IRenderedComponent<Dib4t.Client.Pages.Landing.BudgetDetails>> RenderAsync()
    {
        _dialogs = _context.Render<MudDialogProvider>();
        _context.Render<MudPopoverProvider>();
        _context.Render<MudSnackbarProvider>();
        await _context.Session.GetAuthenticationStateAsync();
        var page = _context.Render<Dib4t.Client.Pages.Landing.BudgetDetails>(p => p.Add(c => c.BudgetId, BudgetId).Add(c => c.Zone, Lisbon));
        page.WaitForAssertion(() => page.FindAll(".budget-tasks-total").Should().NotBeEmpty());
        return page;
    }

    private static BudgetResponse Budget() =>
        new(0, false, false, 25, 45, 60, false, BudgetId, Metal, "Máquina de corte", "Eng.ª Maria",
            new BudgetAuthorResponse([], "Desenvolvimento", UserId, "Ana Administradora", null, 0), "26-0001", "30 dias", 1471, "Texto",
            new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static BudgetResponse Patched(PatchBudgetRequest patch)
    {
        var budget = Budget();
        var number = patch.Value switch
        {
            double d => d,
            string s => Dib4t.SharedKernel.JsNumber.FromText(s),
            _ => double.NaN,
        };
        return patch.PropertyName switch
        {
            "total" => budget with { Total = number },
            "margin" => budget with { Margin = number },
            "complete" => budget with { Complete = true },
            "description" => budget with { Description = (string?)patch.Value },
            _ => budget,
        };
    }

    private static BudgetTaskResponse Task(string id, string type, double unitPrice, double quantity, string? description = "x") =>
        new(id, BudgetId, type, description, unitPrice, quantity, 0);

    private static double Number(object? value) => value switch
    {
        double d => d,
        string s => Dib4t.SharedKernel.JsNumber.FromText(s),
        _ => double.NaN,
    };
}
