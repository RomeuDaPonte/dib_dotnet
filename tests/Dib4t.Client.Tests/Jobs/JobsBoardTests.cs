using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Budgets;
using Dib4t.Client.Components.Jobs;
using Dib4t.Client.Jobs;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Jobs;

/// <summary>The jobs board (legacy <c>components/job/*</c>) and its rules against a substituted job API.</summary>
public sealed class JobsBoardTests : IAsyncDisposable
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    private static readonly EntityResponse Metal = new("5f1e9a2b3c4d5e6f70820001", "Metalúrgica do Ave", null, null, null, null, "30 dias", "Cliente", 0);
    private static readonly DateTimeOffset Today = new(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ClientTestContext _context = new();
    private readonly IJobApi _jobs = Substitute.For<IJobApi>();
    private readonly IClipboard _clipboard = Substitute.For<IClipboard>();
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;
    private IRenderedComponent<MudPopoverProvider> _popovers = null!;

    public JobsBoardTests()
    {
        _context.Services.AddSingleton(_jobs);
        _context.Services.AddSingleton(_clipboard);
        _context.Services.AddSingleton<TimeProvider>(new FixedTime(Today));
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", "canSeeObras"));
        _jobs.CountsAsync().Returns(ApiResult.Success(new JobCountsResponse(13, 3, 1, 2, 3, 2, 2)));
        _jobs.ListByStateAsync(Arg.Any<string>()).Returns(ApiResult.Success<IReadOnlyList<JobResponse<JobListBudgetResponse>>>([]));
        _jobs.ListByStateAsync(JobStates.Development).Returns(ApiResult.Success<IReadOnlyList<JobResponse<JobListBudgetResponse>>>(
        [
            Listed("j1", "26-0001", JobStates.Development, new DateTime(2030, 1, 12, 0, 0, 0, DateTimeKind.Utc), JobPriorities.High),
            Listed("j2", "26-0002", JobStates.Development, new DateTime(2030, 2, 10, 0, 0, 0, DateTimeKind.Utc), JobPriorities.Low) with
            {
                AssemblyStatistic = Assembly("2"), Comments = Stored<string>.Of("Ver nota"),
            },
        ]));
        _jobs.ListByStateAsync(JobStates.ForInvoicing).Returns(ApiResult.Success<IReadOnlyList<JobResponse<JobListBudgetResponse>>>(
        [
            Listed("j3", "26-0003", JobStates.ForInvoicing, new DateTime(2029, 12, 20, 0, 0, 0, DateTimeKind.Utc), JobPriorities.Medium) with
            {
                IsOfficialOrderIdentification = Stored<bool>.Of(true), InvoicedPercent = Stored<double>.Of(60), DeliveryDate = Stored<DateTime>.Of(new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            },
        ]));
        _clipboard.CopyAsync(Arg.Any<string>()).Returns(true);
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task The_first_tab_is_the_users_field_and_the_tabs_show_the_counters()
    {
        var board = await RenderBoardAsync();

        board.WaitForAssertion(() => Numbers(board).Should().Equal("26-0001", "26-0002"));
        board.Find("[role=tab][aria-selected=true]").TextContent.Should().Contain("Desenvolvimento").And.Contain("3");
        board.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().HaveCount(8);
        board.FindAll("[role=tab]")[0].TextContent.Should().Contain("Todas").And.Contain("13");
        board.FindAll("[role=tab]")[7].TextContent.Should().Contain("Faturadas").And.Contain("-");
        await _jobs.Received(1).ListByStateAsync(JobStates.Development);
    }

    [Fact]
    public async Task Rows_show_the_priority_countdown_comments_and_non_conformity()
    {
        var board = await RenderBoardAsync();

        board.WaitForAssertion(() => Numbers(board).Should().HaveCount(2));
        board.FindAll("[aria-label='Prioritário']").Should().HaveCount(1);
        board.FindAll("[aria-label='Não Prioritário']").Should().HaveCount(1);
        board.FindAll("[aria-label='Não conformidade']").Should().HaveCount(1, "26-0002 needed 2 tries");
        board.FindAll("button[aria-label^='Comentários de']").Select(b => b.GetAttribute("aria-label")).Should().Equal("Comentários de 26-0002");
        board.FindAll("input.jobs-due-input").Select(i => i.GetAttribute("value")).Should().Equal("2030-01-12", "2030-02-10");
        board.Markup.Should().Contain("job-countdown-yellow", "2 days left").And.Contain("job-countdown-green").And.NotContain("Ft %");
    }

    [Fact]
    public async Task The_invoicing_tab_shows_billed_percentage_order_and_delivery_and_no_priority_menu()
    {
        var board = await RenderBoardAsync();
        await board.FindAll("[role=tab]")[6].ClickAsync(new MouseEventArgs());

        board.WaitForAssertion(() => Numbers(board).Should().Equal("26-0003"));
        board.Markup.Should().Contain("Ft %").And.Contain("60 %").And.Contain("job-countdown-hidden");
        board.FindAll("[aria-label='Emcomenda']").Should().HaveCount(1);
        board.FindAll("[aria-label='Entregue']").Should().HaveCount(1);
        board.FindAll("button[aria-label='Opções de 26-0003']").Should().BeEmpty();
    }

    [Fact]
    public async Task Eliminar_changes_the_state_at_once_and_the_row_leaves_the_tab()
    {
        _jobs.ChangeStateAsync("j3", JobStates.Deleted).Returns(ApiResult.Success(Detailed("j3", JobStates.Deleted)));
        var board = await RenderBoardAsync();
        await board.FindAll("[role=tab]")[6].ClickAsync(new MouseEventArgs());
        board.WaitForAssertion(() => Numbers(board).Should().Equal("26-0003"));

        await MenuAsync(board, "Alterar estado de 26-0003", "Eliminar", expected: ["Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação", "Faturada", "Eliminar"]);

        board.WaitForAssertion(() => Numbers(board).Should().BeEmpty());
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Estado atualizado com sucesso");
        await _jobs.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
    }

    [Fact]
    public async Task A_state_opens_the_statistics_dialog_that_sends_the_form_with_the_new_state()
    {
        _jobs.GetByIdAsync("j1").Returns(ApiResult.Success(Detailed("j1", JobStates.Development)));
        _jobs.SaveStatisticsAsync("j1", Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(new StatisticsSave(ApiResult.Success(Detailed("j1", JobStates.OnApproval)), null));
        var board = await RenderBoardAsync();
        board.WaitForAssertion(() => Numbers(board).Should().HaveCount(2));

        await MenuAsync(board, "Alterar estado de 26-0001", "Em Aprovação");
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Desenvolvimento | 26-0001"));
        _dialogs.Markup.Should().Contain("Componentes").And.Contain("Automação").And.Contain("Horas orçamentadas").And.NotContain("Materiais");
        await _dialogs.Find("#job-hours").InputAsync(new ChangeEventArgs { Value = "18" });
        _dialogs.Find("#job-deviation").GetAttribute("value").Should().Be("2.5", "20.5 budgeted − 18");
        await _dialogs.FindAll("button").First(b => b.TextContent.Trim() == "Concluir").ClickAsync(new MouseEventArgs());

        await _jobs.Received(1).SaveStatisticsAsync("j1", Arg.Is<IReadOnlyDictionary<string, object?>>(b =>
            Equals(b["state"], JobStates.OnApproval) && Equals(b["numberOfHoursUsed"], "18") && Equals(b["deviation"], 2.5) && Equals(b["budgetedHours"], 20.5)));
        board.WaitForAssertion(() => Numbers(board).Should().Equal(["26-0002"], "the job left the development tab"));
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Estado atualizado com sucesso!");
    }

    [Fact]
    public async Task Billing_below_100_percent_warns_and_keeps_the_job()
    {
        _jobs.GetByIdAsync("j3").Returns(ApiResult.Success(Detailed("j3", JobStates.ForInvoicing)));
        _jobs.SaveStatisticsAsync("j3", Arg.Any<IReadOnlyDictionary<string, object?>>()).Returns(new StatisticsSave(
            ApiResult.Failure<JobResponse<BudgetResponse>>(HttpStatusCode.MethodNotAllowed, "{}"),
            new JobStatisticsRefusedResponse("Valor faturado inferior a 100%", Detailed("j3", JobStates.ForInvoicing))));
        var board = await RenderBoardAsync();
        await board.FindAll("[role=tab]")[6].ClickAsync(new MouseEventArgs());
        board.WaitForAssertion(() => Numbers(board).Should().Equal("26-0003"));

        await MenuAsync(board, "Alterar estado de 26-0003", "Faturada");
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("Identificação da encomenda"));
        await _dialogs.Find("#job-invoiced").InputAsync(new ChangeEventArgs { Value = "150" });
        _dialogs.Markup.Should().Contain("Min 0 max 100");
        await _dialogs.Find("#job-invoiced").InputAsync(new ChangeEventArgs { Value = "80" });
        await _dialogs.FindAll("button").First(b => b.TextContent.Trim() == "Concluir").ClickAsync(new MouseEventArgs());

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Valor faturado inferior a 100%" && s.Severity == Severity.Warning);
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().NotContain("Identificação da encomenda"));
        Numbers(board).Should().Equal("26-0003");
    }

    [Fact]
    public async Task A_priority_reorders_the_rows_and_the_copy_path_is_the_job_folder()
    {
        _jobs.ChangePriorityAsync("j2", JobPriorities.High).Returns(ApiResult.Success(Detailed("j2", JobStates.Development) with { Priority = JobPriorities.High }));
        var board = await RenderBoardAsync();
        board.WaitForAssertion(() => Numbers(board).Should().Equal("26-0001", "26-0002"));

        await MenuAsync(board, "Opções de 26-0002", "Prioritária", expected: ["Prioritária", "Intermédia", "Não prioritária"]);
        board.WaitForAssertion(() => Numbers(board).Should().Equal(["26-0001", "26-0002"], "both high now: by due date"));
        await board.Find("button[aria-label='Copiar caminho de 26-0001']").ClickAsync(new MouseEventArgs());

        await _clipboard.Received(1).CopyAsync(@"D:\NAS\Obras\2026\Metalúrgica do Ave\26-0001 - Máquina 26-0001\CAD");
    }

    [Fact]
    public void The_board_rules_follow_the_legacy_components()
    {
        JobBoard.InitialTab(JobStates.Machining).Should().Be(4);
        JobBoard.InitialTab(null).Should().Be(0);
        JobBoard.DaysLeft(new DateTime(2030, 1, 9, 0, 0, 0, DateTimeKind.Utc), Today, Lisbon).Should().Be(-1);
        JobBoard.DaysLeft(new DateTime(2030, 1, 10, 0, 0, 0, DateTimeKind.Utc), Today, Lisbon).Should().Be(0);
        JobBoard.Countdown(0).Color.Should().Be("job-countdown-red");
        JobBoard.Countdown(4).Color.Should().Be("job-countdown-yellow");
        JobBoard.Countdown(9).Color.Should().Be("job-countdown-green");
        JobBoard.Countdown(10).Icon.Should().Be(JobIcons.NinePlus);
        JobBoard.CopyPath("26-0001", "C", "D", JobStates.Machining).Should().Be(@"D:\NAS\Obras\2026\C\26-0001 - D\CAM");
        JobBoard.CopyPath("26-0001", "C", "D", JobStates.Assembly).Should().Be(@"D:\NAS\Obras\2026\C\26-0001 - D");
        JobBoard.Choose(JobStates.Billed, JobStates.ForInvoicing).Should().Be(StateChoice.ChangeNow, "D1 fixed: moving out of Faturada works");
        JobBoard.Choose(JobStates.Machining, JobStates.Assembly).Should().Be(StateChoice.OpenStatistics);
        JobBoard.StateMenu(JobStates.Machining).Select(i => i.Label).Should().NotContain("Eliminar");
    }

    [Fact]
    public void The_comments_dialog_sends_the_jobs_own_billed_percentage()
    {
        var body = new JobCommentsForm(Detailed("j5", JobStates.Machining) with { InvoicedPercent = Stored<double>.Of(30) }).ToBody();

        body["invoicedPercent"].Should().Be(30.0, "legacy leaves it out: machining refuses the save");
        body.Keys.Should().Contain("materials").And.NotContain("components");
    }

    private static List<string> Numbers(IRenderedComponent<JobsBoard> board) =>
        [.. board.FindAll("td.jobs-number").Select(td => td.TextContent.Trim())];

    private async Task MenuAsync(IRenderedComponent<JobsBoard> board, string menu, string item, string[]? expected = null)
    {
        await board.Find($"button[aria-label='{menu}']").ClickAsync(new MouseEventArgs());
        _popovers.WaitForAssertion(() => _popovers.FindAll(".mud-menu-item").Should().Contain(i => i.TextContent.Trim() == item));
        if (expected is not null)
        {
            _popovers.FindAll(".mud-menu-item").Select(i => i.TextContent.Trim()).Should().Equal(expected);
        }

        await _popovers.FindAll(".mud-menu-item").First(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    private async Task<IRenderedComponent<JobsBoard>> RenderBoardAsync()
    {
        await _context.Session.GetAuthenticationStateAsync();
        _popovers = _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return _context.Render<JobsBoard>(p => p.Add(c => c.Zone, Lisbon));
    }

    private static readonly Stored<double> Zero = Stored<double>.Of(0);

    private static JobAssemblyStatisticResponse Assembly(string tries) => new(Zero, 10, 10, "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", tries, "a", null);

    private static JobResponse<JobListBudgetResponse> Listed(string id, string number, string state, DateTime due, string priority) =>
        new(Stored<double>.Of(0), 15000.5, state, "PO", Stored<bool>.Of(false), Zero, Zero, Zero, Zero, Zero, [], id,
            new JobListBudgetResponse(0, true, true, 35, 45, 30, true, "b-" + id, Metal, "Máquina " + number, "Eng.ª", "u1", number, "30 dias", 1, null,
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 0),
            "Ana", due, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), new JobHoursResponse(Zero, 20.5, 20.5, "d"), null, null, priority, 0);

    private static JobResponse<BudgetResponse> Detailed(string id, string state)
    {
        var number = "26-000" + id[^1];
        return new(Stored<double>.Of(0), 15000.5, state, "PO", Stored<bool>.Of(true), Zero, Zero, Zero, Zero, Zero, [], id,
            new BudgetResponse(0, true, true, 35, 45, 30, true, "b-" + id, Metal, "Máquina " + number, "Eng.ª", null, number, "30 dias", 1, null,
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 0),
            "Ana", new DateTime(2030, 1, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), new JobHoursResponse(Zero, 20.5, 20.5, "d"),
            new JobMachiningStatisticResponse(Zero, 15, 15, "n/a", "n/a", "n/a", "m", null), Assembly("1"), JobPriorities.Medium, 0);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
