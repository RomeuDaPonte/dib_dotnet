using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Budgets;
using Dib4t.Client.Components.Budgets;
using Dib4t.Contracts.Account;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Budgets;

/// <summary>The budgets page (legacy <c>components/budgets/*</c>) and its rules against substituted API clients.</summary>
public sealed class BudgetsPageTests : IAsyncDisposable
{
    private const string UserId = "5f1e9a2b3c4d5e6f70819203";
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";

    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    private static readonly EntityResponse Metal = new(ClientId, "Metalúrgica do Ave", null, null, null, null, "30 dias", "Cliente", 0);
    private static readonly EntityResponse Cortica = new("5f1e9a2b3c4d5e6f70820002", "Cortiças Reunidas", null, null, null, null, "30 dias", "Cliente e Fornecedor", 0);

    private readonly ClientTestContext _context = new();
    private readonly IBudgetApi _budgets = Substitute.For<IBudgetApi>();
    private readonly IUsersApi _users = Substitute.For<IUsersApi>();
    private readonly IClipboard _clipboard = Substitute.For<IClipboard>();
    private readonly IFileDownload _downloads = Substitute.For<IFileDownload>();
    private readonly ITaskApi _tasks = Substitute.For<ITaskApi>();
    private readonly IPricesApi _prices = Substitute.For<IPricesApi>();
    private readonly IPaymentMethodsApi _paymentMethods = Substitute.For<IPaymentMethodsApi>();
    private readonly IJobApi _jobs = Substitute.For<IJobApi>();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 7, 31, 23, 30, 0, TimeSpan.Zero)); // 1 August in Lisbon
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;
    private IRenderedComponent<MudPopoverProvider> _popovers = null!;

    public BudgetsPageTests()
    {
        _context.Services.AddSingleton(_budgets);
        _context.Services.AddSingleton(_users);
        _context.Services.AddSingleton(_clipboard);
        _context.Services.AddSingleton(_downloads);
        _context.Services.AddSingleton(_tasks);
        _context.Services.AddSingleton(_prices);
        _context.Services.AddSingleton(_paymentMethods);
        _context.Services.AddSingleton(_jobs);
        _context.Services.AddSingleton<TimeProvider>(_time);
        _context.Services.AddTransient<BudgetDetailsState>();
        _tasks.ListAsync(Arg.Any<string>()).Returns(ApiResult.Success<IReadOnlyList<BudgetTaskResponse>>([]));
        _tasks.ListTypesAsync().Returns(ApiResult.Success<IReadOnlyList<string>>(["automation"]));
        _prices.GetAsync().Returns(ApiResult.Success(new Dib4t.Contracts.Settings.PricesResponse(32.5, 10, 10, 10, 10, 35)));
        _paymentMethods.ListAsync().Returns(ApiResult.Success<IReadOnlyList<Dib4t.Contracts.Settings.PaymentMethodResponse>>([]));
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", "canSeeOrcamentos"));
        _budgets.ListAsync().Returns(ApiResult.Success<IReadOnlyList<BudgetListItemResponse>>(
        [
            Row("b3", "26-0003", "Metalúrgica do Ave", revision: 2, complete: true, pdf: true),
            Row("b2", "26-0002", "Cortiças Reunidas", hasJob: true, complete: true, pdf: true),
            Row("b1", "26-0001", "Metalúrgica do Ave"),
        ]));
        _budgets.ListClientsAsync().Returns(ApiResult.Success<IReadOnlyList<EntityResponse>>([Cortica, Metal]));
        _users.ListAsync().Returns(ApiResult.Failure<IReadOnlyList<UserListItemResponse>>(HttpStatusCode.Unauthorized, "Não tem permissão para concluir esta acção."));
        _clipboard.CopyAsync(Arg.Any<string>()).Returns(true);
        _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(ApiResult.Success<JobResponse<BudgetRefsResponse>?>(null));
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Rows_show_the_legacy_columns_icons_and_dates_in_server_order()
    {
        var table = await RenderTableAsync();

        Numbers(table).Should().Equal("26-0003", "26-0002", "26-0001");
        table.Markup.Should().Contain("1-3 of 3").And.Contain("Tabela densa").And.Contain("Novo orçamento").And.Contain("01/02/2026");
        table.FindAll("[aria-label='Incompleto']").Should().HaveCount(1, "only 26-0001 has no PDF");
        table.FindAll("[aria-label='contem obra']").Should().HaveCount(1);
        table.FindAll("button[aria-label^='Revisões de']").Select(b => b.GetAttribute("aria-label")).Should().Equal(["Revisões de 26-0003"], "only revision > 0");
    }

    [Fact]
    public async Task Revisions_are_read_when_a_row_opens_and_hidden_again()
    {
        _budgets.ListRevisionsAsync("26-0003").Returns(ApiResult.Success<IReadOnlyList<BudgetRevisionResponse>>(
        [
            Revision("r2", 2, "Bruno Silva"), Revision("r1", 1, "Ana Administradora"), Revision("b0", 0, "Ana Administradora") with { WasPdfDownloaded = false },
        ]));
        var table = await RenderTableAsync();

        await table.Find("button[aria-label='Revisões de 26-0003']").ClickAsync(new MouseEventArgs());
        table.WaitForAssertion(() => table.Markup.Should().Contain("Nª da revisão").And.Contain("R-2").And.Contain("R-0").And.Contain("Bruno Silva"));
        table.Find("button[aria-label='Revisões de 26-0003']").GetAttribute("aria-expanded").Should().Be("true");
        await table.Find("button[aria-label='Revisões de 26-0003']").ClickAsync(new MouseEventArgs());

        table.Markup.Should().NotContain("Nª da revisão");
        await _budgets.Received(1).ListRevisionsAsync("26-0003");
    }

    [Fact]
    public async Task Enter_searches_on_the_server_and_an_empty_text_shows_the_list_again()
    {
        _budgets.SearchAsync("corti").Returns(ApiResult.Success<IReadOnlyList<BudgetListItemResponse>>([Row("b2", "26-0002", "Cortiças Reunidas")]));
        _budgets.SearchAsync("((").Returns(ApiResult.Failure<IReadOnlyList<BudgetListItemResponse>>(HttpStatusCode.BadRequest, "Error performing search: x"));
        var table = await RenderTableAsync();

        await SearchAsync(table, "corti");
        Numbers(table).Should().Equal("26-0002");
        await SearchAsync(table, "((");
        Numbers(table).Should().BeEmpty("legacy empties the list when the search fails");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Error performing search: x");
        await SearchAsync(table, string.Empty);
        Numbers(table).Should().Equal("26-0003", "26-0002", "26-0001");
        await _budgets.DidNotReceive().SearchAsync(string.Empty);
    }

    [Fact]
    public async Task A_new_budget_is_checked_like_the_legacy_yup_schema_and_joins_the_top_of_the_list()
    {
        _budgets.CreateAsync(Arg.Any<NewBudgetRequest>()).Returns(ApiResult.Success(new BudgetSaveResult(Saved("b4", "26-0004", 0, Metal), null)));
        var table = await RenderTableAsync();

        await table.FindAll("button").First(b => b.TextContent.Trim() == "Novo orçamento").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Find("#budget-form"));
        _dialogs.FindAll("#budget-made-by option").Select(o => o.GetAttribute("value")).Should().Equal([UserId], "without the users list (S route) only the signed-in user");
        await _dialogs.Find("#budget-form").SubmitAsync();
        _dialogs.Markup.Should().Contain("client is a required field").And.Contain("descriptive is a required field").And.Contain("personInCharge is a required field");
        await _dialogs.Find("#budget-descriptive").InputAsync(new ChangeEventArgs { Value = "Suporte 30/40" });
        _dialogs.Markup.Should().Contain("Carácteres inválidos", "validated on every change after the first submit; no / (D21)");
        await _dialogs.Find("#budget-descriptive").InputAsync(new ChangeEventArgs { Value = new string('a', 31) });
        _dialogs.Markup.Should().Contain("Não pode exceder mais de 25 caracteres");
        await _budgets.DidNotReceiveWithAnyArgs().CreateAsync(default!);

        var form = table.FindComponent<BudgetDialog>().Instance.Form!;
        form.Client = Metal;
        await _dialogs.Find("#budget-descriptive").InputAsync(new ChangeEventArgs { Value = "Máquina de corte" });
        await _dialogs.Find("#budget-person").InputAsync(new ChangeEventArgs { Value = "Eng.ª Maria" });
        await _dialogs.Find("#budget-form").SubmitAsync();

        await _budgets.Received(1).CreateAsync(new NewBudgetRequest("Máquina de corte", "Eng.ª Maria", UserId, ClientId));
        table.WaitForAssertion(() => Numbers(table).Should().Equal("26-0004", "26-0003", "26-0002", "26-0001"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Orçamento criado com sucesso!" && s.Severity == Severity.Success);
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("#budget-form").Should().BeEmpty());
    }

    [Fact]
    public async Task A_supplier_notice_and_a_server_text_keep_the_dialog_open()
    {
        _budgets.CreateAsync(Arg.Any<NewBudgetRequest>()).Returns(
            ApiResult.Success(new BudgetSaveResult(null, "Entidade registada como fornecedor!")),
            ApiResult.Failure<BudgetSaveResult>(HttpStatusCode.BadRequest, "Cliente não existe!"));
        var table = await RenderTableAsync();
        await table.FindAll("button").First(b => b.TextContent.Trim() == "Novo orçamento").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Find("#budget-form"));
        var form = table.FindComponent<BudgetDialog>().Instance.Form!;
        (form.Client, form.Descriptive, form.PersonInCharge) = (Metal, "Prensa", "X");

        await _dialogs.Find("#budget-form").SubmitAsync();
        await _dialogs.Find("#budget-form").SubmitAsync();

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Entidade registada como fornecedor!" && s.Severity == Severity.Warning);
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Cliente não existe!" && s.Severity == Severity.Error);
        _dialogs.FindAll("#budget-form").Should().ContainSingle();
        Numbers(table).Should().HaveCount(3);
    }

    [Fact]
    public async Task Clone_starts_from_the_row_with_the_client_found_by_name_and_a_new_revision_replaces_its_row()
    {
        _budgets.CloneAsync("b2", Arg.Any<NewBudgetRequest>()).Returns(ApiResult.Success(new BudgetSaveResult(Saved("b5", "26-0005", 0, Cortica), null)));
        _budgets.CreateRevisionAsync("b3").Returns(ApiResult.Success(Saved("b3r3", "26-0003", 3, Metal)));
        var table = await RenderTableAsync();

        await MenuAsync(table, "Opções de 26-0002 R0", "Clonar");
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Clonar orçamento"));
        var form = table.FindComponent<BudgetDialog>().Instance.Form!;
        (form.Client, form.Descriptive, form.MadeById).Should().Be((Cortica, "Descritivo 26-0002", UserId));
        await _dialogs.Find("#budget-form").SubmitAsync();
        _dialogs.Markup.Should().Contain("Carácteres inválidos", "the legacy schema has no '-': a copied descriptive must be retyped, as in legacy");
        await _dialogs.Find("#budget-descriptive").InputAsync(new ChangeEventArgs { Value = "Copia 2" });
        await _dialogs.Find("#budget-form").SubmitAsync();
        await _budgets.Received(1).CloneAsync("b2", new NewBudgetRequest("Copia 2", "Eng.ª Maria", UserId, Cortica.Id));
        table.WaitForAssertion(() => Numbers(table).First().Should().Be("26-0005"));

        await MenuAsync(table, "Opções de 26-0003 R2", "Nova revisão");
        table.WaitForAssertion(() => table.Find("button[aria-label='Editar 26-0003 R3']"));
        Numbers(table).Should().Equal("26-0005", "26-0003", "26-0002", "26-0001");
    }

    [Fact]
    public async Task Obter_pdf_saves_the_legacy_file_name_and_the_row_shows_the_PDF_as_downloaded()
    {
        _budgets.ListAsync().Returns(ApiResult.Success<IReadOnlyList<BudgetListItemResponse>>(
            [Row("b3", "26-0003", "Metalúrgica do Ave", revision: 2, complete: true), Row("b1", "26-0001", "Metalúrgica do Ave", complete: true)]));
        _budgets.GetPdfAsync("b3").Returns(ApiResult.Success("JVBERi0xLjM="));
        _budgets.GetPdfAsync("b1").Returns(ApiResult.Success("JVBERi0xLjM="));
        var table = await RenderTableAsync();

        await MenuAsync(table, "Opções de 26-0003 R2", "Obter pdf");
        await MenuAsync(table, "Opções de 26-0001 R0", "Obter pdf");

        await _downloads.Received(1).SaveBase64Async("Prop - 26-0003 R2.pdf", "JVBERi0xLjM=", "application/pdf");
        await _downloads.Received(1).SaveBase64Async("Prop - 26-0001.pdf", "JVBERi0xLjM=", "application/pdf");
        BudgetList.WithPdfDownloaded([Row("b3", "26-0003", "M", complete: true), Row("b1", "26-0001", "M")], "b3")
            .Select(r => r.WasPdfDownloaded).Should().Equal(true, false);
    }

    [Fact]
    public async Task Obter_pdf_is_offered_only_for_a_complete_budget_and_a_refused_PDF_saves_nothing()
    {
        _budgets.GetPdfAsync("b3").Returns(ApiResult.Failure<string>(HttpStatusCode.BadRequest, "Orçamento não existe"));
        var table = await RenderTableAsync();

        await table.Find("button[aria-label='Opções de 26-0001 R0']").ClickAsync(new MouseEventArgs());
        _popovers.WaitForAssertion(() => _popovers.FindAll(".mud-menu-item").Should().Contain(i => i.TextContent.Contains("Clonar", StringComparison.Ordinal)));
        _popovers.FindAll(".mud-menu-item").Should().NotContain(i => i.TextContent.Contains("Obter pdf", StringComparison.Ordinal), "26-0001 is not complete");

        await MenuAsync(table, "Opções de 26-0003 R2", "Obter pdf");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Orçamento não existe");
        await _downloads.DidNotReceiveWithAnyArgs().SaveBase64Async(default!, default!, default!);
    }

    [Fact]
    public async Task The_copy_path_is_the_legacy_one()
    {
        var table = await RenderTableAsync();

        await table.Find("button[aria-label='Copiar caminho de 26-0001']").ClickAsync(new MouseEventArgs());
        await table.Find("button[aria-label='Copiar caminho de 26-0002']").ClickAsync(new MouseEventArgs());

        await _clipboard.Received(1).CopyAsync("\\\\DIB4T\\Orçamentos\\2026\\Metalúrgica do Ave\\26-0001 - Descritivo 26-0001");
        await _clipboard.Received(1).CopyAsync("D:\\NAS\\Obras\\2026\\Cortiças Reunidas\\26-0002 - Descritivo 26-0002");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Link copiado!");
    }

    [Fact]
    public void Copy_paths_menus_and_rows_follow_the_legacy_rules()
    {
        var row = Row("b", "25-0100", "C", hasJob: true) with { Date = new DateTime(2025, 12, 31, 23, 30, 0, DateTimeKind.Utc) };
        BudgetList.CopyPath(row with { HasGenerateJob = false }, Lisbon).Should().Be("\\\\DIB4T\\Orçamentos\\2025\\C\\25-0100 - Descritivo 25-0100");
        BudgetList.CopyPath(row with { HasGenerateJob = false }, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).Should().Contain("\\2026\\", "the browser's year");
        BudgetList.CopyPath(row, Lisbon, new JobFolder(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "Em Aprovação")).Should().Be("D:\\NAS\\Obras\\2026\\C\\25-0100 - Descritivo 25-0100\\CAD");
        BudgetList.CopyPath(row, Lisbon, new JobFolder(null, "Maquinação")).Should().EndWith("\\Obras\\2025\\C\\25-0100 - Descritivo 25-0100\\CAM");
        BudgetList.CopyPath(row, Lisbon, new JobFolder(null, "Montagem")).Should().EndWith("25-0100 - Descritivo 25-0100");

        var all = Enum.GetValues<BudgetMenuItem>().ToHashSet();
        BudgetList.Menu(false, false, false, all).Should().Equal(BudgetMenuItem.Clone);
        BudgetList.Menu(true, false, false, all).Should().Equal(BudgetMenuItem.Pdf, BudgetMenuItem.Clone, BudgetMenuItem.NewRevision);
        BudgetList.Menu(true, true, false, all).Should().Equal(BudgetMenuItem.Pdf, BudgetMenuItem.Clone, BudgetMenuItem.NewRevision, BudgetMenuItem.CreateJob);
        BudgetList.Menu(true, true, true, all).Should().Equal(BudgetMenuItem.Pdf, BudgetMenuItem.JobDetails, BudgetMenuItem.Print, BudgetMenuItem.Clone);
        BudgetList.Menu(true, true, false).Should().Equal(BudgetMenuItem.Pdf, BudgetMenuItem.Clone, BudgetMenuItem.NewRevision, BudgetMenuItem.CreateJob);
        BudgetList.Menu(true, true, true).Should().Equal([BudgetMenuItem.Pdf, BudgetMenuItem.JobDetails, BudgetMenuItem.Clone], "\"Imprimir\" comes with Phase 4 step 4");

        BudgetList.Date(new DateTime(2026, 5, 24, 23, 30, 0, DateTimeKind.Utc), Lisbon).Should().Be("25/05/2026");
        BudgetForm.FilterClients([Metal, Cortica], "cortica").Should().Equal(Cortica);
        BudgetForm.FilterClients([Metal, Cortica], " ").Should().Equal(Metal, Cortica);
    }

    [Fact]
    public async Task The_page_needs_canSeeOrcamentos()
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Bruno", "canSeeObras"));
        await _context.Session.GetAuthenticationStateAsync();

        var page = Render<Dib4t.Client.Pages.Landing.Budgets>();

        page.Markup.Should().Contain("Não tem permissão para concluir esta acção.").And.NotContain("Novo orçamento");
        await _budgets.DidNotReceive().ListAsync();
    }

    [Fact]
    public async Task The_details_header_edits_the_budget_without_changing_the_client()
    {
        _budgets.GetAsync("b3").Returns(ApiResult.Success(Saved("b3", "26-0003", 2, Metal)));
        _budgets.UpdateAsync("b3", Arg.Any<UpdateBudgetRequest>()).Returns(ApiResult.Success(Saved("b3", "26-0003", 2, Metal) with { Descriptive = "Novo" }));
        await _context.Session.GetAuthenticationStateAsync();
        var page = Render<Dib4t.Client.Pages.Landing.BudgetDetails>(p => p.Add(c => c.BudgetId, "b3").Add(c => c.Zone, Lisbon));
        page.WaitForAssertion(() => page.Markup.Should().Contain("26-0003 | R 2"));

        await page.Find("button[aria-label='Editar orçamento']").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Editar orçamento"));
        _dialogs.Find("#budget-client").HasAttribute("disabled").Should().BeTrue();
        await _dialogs.Find("#budget-descriptive").InputAsync(new ChangeEventArgs { Value = "Novo" });
        await _dialogs.Find("#budget-form").SubmitAsync();

        await _budgets.Received(1).UpdateAsync("b3", new UpdateBudgetRequest(UserId, "Novo", "Eng.ª Maria"));
        page.WaitForAssertion(() => page.Markup.Should().Contain(">Novo<"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Orçamento editado com sucesso!");
    }

    [Fact]
    public async Task Criar_obra_opens_the_legacy_dialog_with_the_total_and_the_due_date_and_checks_it_like_yup()
    {
        var table = await RenderTableAsync();

        await MenuAsync(table, "Opções de 26-0003 R2", "Criar obra");
        _dialogs.WaitForAssertion(() => _dialogs.Find("#job-form"));
        _dialogs.Find("#job-final-price").GetAttribute("value").Should().Be("43.5");
        _dialogs.Find("#job-due-date").GetAttribute("value").Should().Be("2026-09-30", "1 August (Lisbon) + 60 days; legacy shows the UTC date, 29 September");
        _dialogs.FindAll("#job-initial-state option").Select(o => o.TextContent).Should().Equal(
            "Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação");
        _dialogs.Markup.Should().Contain("Preço final €").And.Contain("Data de entrega").And.Contain("Estado inicial")
            .And.Contain("Identificação da encomenda").And.Contain("Identificação oficial");

        await _dialogs.Find("#job-form").SubmitAsync();
        _dialogs.Markup.Should().Contain("orderIdentification is a required field");
        await _dialogs.Find("#job-final-price").InputAsync(new ChangeEventArgs { Value = "12,5" });
        await _dialogs.Find("#job-due-date").InputAsync(new ChangeEventArgs { Value = "" });
        _dialogs.Markup.Should().Contain("finalPrice must be a `number` type, but the final value was: `NaN` (cast from the value `\"12,5\"`).")
            .And.Contain("dueDate must be a `date` type, but the final value was: `Invalid Date` (cast from the value `\"\"`).");
        await _jobs.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task A_created_job_goes_to_the_jobs_board_a_refusal_keeps_the_dialog_open()
    {
        _jobs.CreateAsync(Arg.Any<NewJobRequest>()).Returns(
            ApiResult.Failure<JobResponse<BudgetResponse>>(HttpStatusCode.BadRequest, "Obra baseada neste orçamento já foi criada anteriormente"),
            ApiResult.Success(Job("b3")));
        var table = await RenderTableAsync();
        await MenuAsync(table, "Opções de 26-0003 R2", "Criar obra");
        _dialogs.WaitForAssertion(() => _dialogs.Find("#job-form"));
        await _dialogs.Find("#job-order").InputAsync(new ChangeEventArgs { Value = "PO-7" });
        await _dialogs.Find("#job-initial-state").ChangeAsync(new ChangeEventArgs { Value = "Montagem" });

        await _dialogs.Find("#job-form").SubmitAsync();
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Obra baseada neste orçamento já foi criada anteriormente" && s.Severity == Severity.Error);
        _dialogs.FindAll("#job-form").Should().ContainSingle("a refused job keeps the dialog open");
        await _dialogs.Find("#job-final-price").InputAsync(new ChangeEventArgs { Value = " 1200.50 " });
        await _dialogs.Find("#job-form").SubmitAsync();

        await _jobs.Received(1).CreateAsync(Arg.Is<NewJobRequest>(r => Equals(r.FinalPrice, 43.5) && r.State == "Montagem" && r.OrderIdentification == "PO-7"
            && r.DueDate == "2026-09-30" && !r.IsOfficialOrderIdentification && r.BudgetId == "b3"));
        await _jobs.Received(1).CreateAsync(Arg.Is<NewJobRequest>(r => Equals(r.FinalPrice, " 1200.50 ")));
        table.WaitForAssertion(() => _dialogs.FindAll("#job-form").Should().BeEmpty());
        _context.Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/landing/job", "legacy goes to the jobs board");
    }

    [Fact]
    public async Task Detalhes_obra_opens_the_job_of_the_budget_and_shows_the_servers_text_without_one()
    {
        _jobs.FindIdByBudgetIdAsync("b2").Returns(
            ApiResult.Failure<string>(HttpStatusCode.BadRequest, "Não há obra corresponde a esse orçamento"),
            ApiResult.Success("5f1e9a2b3c4d5e6f70860001"));
        var table = await RenderTableAsync();

        await MenuAsync(table, "Opções de 26-0002 R0", "Detalhes obra");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Não há obra corresponde a esse orçamento" && s.Severity == Severity.Error);
        await MenuAsync(table, "Opções de 26-0002 R0", "Detalhes obra");

        _context.Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/landing/job/5f1e9a2b3c4d5e6f70860001");
    }

    [Fact]
    public void The_job_form_follows_the_legacy_dialog()
    {
        var lisbon = Lisbon;
        var noTotal = JobForm.For("b", null, 0, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), lisbon);
        noTotal.DueDate.Should().Be("2026-03-01", "0 days: today");
        noTotal.Validate().FinalPrice.Should().Be("finalPrice is a required field");
        JobForm.For("b", 10, 7.9, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), lisbon).DueDate.Should().Be("2026-03-08", "setDate truncates");
        var form = JobForm.For("b", 1e21, 0, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), lisbon);
        form.FinalPrice.Should().Be("1e+21");
        form.ToRequest().FinalPrice.Should().Be(1e21, "untouched: the number");
        form.FinalPrice = "0x10";
        form.OrderIdentification = "x";
        form.Validate().Should().Be(JobFormErrors.None, "yup reads 0x10 as 16 (the server's Joi refuses it)");
        form.ToRequest().FinalPrice.Should().Be("0x10");
    }

    private static readonly Stored<double> Zero = Stored<double>.Of(0);

    private static JobResponse<BudgetResponse> Job(string budgetId) => new(Stored<double>.Of(0), 43.5, "Montagem", "PO-7", Stored<bool>.Of(false), Zero, Zero, Zero, Zero, Zero, [], "j1",
        Saved(budgetId, "26-0003", 2, Metal) with { HasGenerateJob = true }, "Ana Administradora", null, null, null, null, null, "Média", 0);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BudgetListItemResponse Row(string id, string number, string client, double revision = 0, bool complete = false, bool pdf = false, bool hasJob = false) =>
        new(id, number, client, "Descritivo " + number, "Eng.ª Maria", "Ana Administradora", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            revision, complete, pdf, hasJob, 43.5, 60);

    private static BudgetRevisionResponse Revision(string id, double revision, string author) =>
        new(revision, true, true, 35, 60, 30, false, id, ClientId, "Descritivo", "Eng.ª Maria",
            new BudgetAuthorResponse([], "Desenvolvimento", UserId, author, null, 0), "26-0003", "30 dias", 43.5, null,
            new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static BudgetResponse Saved(string id, string number, double revision, EntityResponse client) =>
        new(revision, false, false, 35, 60, 30, false, id, client, "Descritivo " + number, "Eng.ª Maria",
            new BudgetAuthorResponse([], "Desenvolvimento", UserId, "Ana Administradora", null, 0), number, "30 dias", 43.5, null,
            new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static IEnumerable<string> Numbers(IRenderedComponent<BudgetsTable> table) =>
        table.FindAll("tbody tr td.budgets-number").Select(td => td.TextContent.Trim());

    private static async Task SearchAsync(IRenderedComponent<BudgetsTable> table, string text)
    {
        await table.Find("#budgets-filter").InputAsync(new ChangeEventArgs { Value = text });
        await table.Find("#budgets-filter").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
    }

    private async Task MenuAsync(IRenderedComponent<BudgetsTable> table, string menu, string item)
    {
        await table.Find($"button[aria-label='{menu}']").ClickAsync(new MouseEventArgs());
        _popovers.WaitForAssertion(() => _popovers.FindAll(".mud-menu-item").Should().Contain(i => i.TextContent.Contains(item, StringComparison.Ordinal)));
        await _popovers.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(item, StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());
    }

    private async Task<IRenderedComponent<BudgetsTable>> RenderTableAsync()
    {
        await _context.Session.GetAuthenticationStateAsync();
        return Render<BudgetsTable>(p => p.Add(c => c.Zone, Lisbon));
    }

    private IRenderedComponent<TComponent> Render<TComponent>(Action<ComponentParameterCollectionBuilder<TComponent>>? parameters = null)
        where TComponent : IComponent
    {
        _popovers = _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return parameters is null ? _context.Render<TComponent>() : _context.Render(parameters);
    }
}
