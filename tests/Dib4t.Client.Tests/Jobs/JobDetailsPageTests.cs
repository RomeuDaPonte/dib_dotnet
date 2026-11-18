using System.Net;
using System.Text.Json;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Jobs;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Jobs;
using Dib4t.Contracts.Settings;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using Dib4t.Tests.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Jobs;

/// <summary>
/// The job details page (legacy <c>components/jobDetais/*</c>) against substituted APIs. The cost texts expected here
/// are those of <c>job-costs-golden.json</c> (the legacy front-end's code run in Node) for the same tasks, prices and
/// values; the full set of scenarios is checked in <c>JobCostCalculationsTests</c>.
/// </summary>
public sealed class JobDetailsPageTests : IAsyncDisposable
{
    private const string JobId = "5f1e9a2b3c4d5e6f70860004";
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810004";
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    private static readonly EntityResponse Metal = new("5f1e9a2b3c4d5e6f70820001", "Metalúrgica do Ave", null, null, null, null, "30 dias", "Cliente", 0);
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobCostsGolden));

    private readonly ClientTestContext _context = new();
    private readonly IJobApi _jobs = Substitute.For<IJobApi>();
    private readonly ITaskApi _tasks = Substitute.For<ITaskApi>();
    private readonly IPricesApi _prices = Substitute.For<IPricesApi>();

    public JobDetailsPageTests()
    {
        _context.Services.AddSingleton(_jobs);
        _context.Services.AddSingleton(_tasks);
        _context.Services.AddSingleton(_prices);
        _context.Services.AddTransient<JobDetailsState>();
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", "canSeeObras"));
        var scenario = Scenario("hours and costs registered");
        _tasks.ListAsync(BudgetId).Returns(ApiResult.Success<IReadOnlyList<BudgetTaskResponse>>([.. scenario.GetProperty("tasks").EnumerateArray().Select((t, i) =>
            new BudgetTaskResponse("t" + i, BudgetId, t.GetProperty("taskType").GetString(), "", t.GetProperty("unitPrice").GetDouble(), t.GetProperty("quantity").GetDouble(), 0))]));
        var p = scenario.GetProperty("prices");
        _prices.GetAsync().Returns(ApiResult.Success(new PricesResponse(p.GetProperty("automation").GetDouble(), p.GetProperty("consultancy").GetDouble(),
            p.GetProperty("development").GetDouble(), p.GetProperty("machining").GetDouble(), p.GetProperty("assembly").GetDouble(), p.GetProperty("margin").GetDouble())));
        _jobs.GetByIdAsync(JobId).Returns(ApiResult.Success(Job(JobStates.Machining)));
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task The_header_and_the_costs_show_what_the_legacy_page_shows()
    {
        var page = await RenderAsync();

        page.Find(".job-number").TextContent.Should().Be("26-0004");
        page.Markup.Should().Contain("Cliente: Metalúrgica do Ave").And.Contain("Descritivo: Máquina de corte").And.Contain("ATT: Eng.ª Maria")
            .And.Contain("Ana Administradora").And.Contain("Maquinação");
        page.Find("[data-testid=job-start-date]").TextContent.Should().Be("2026-02-02", "the start date in Lisbon");
        page.Find("#job-order-identification").GetAttribute("value").Should().Be("ENC-2030/001");
        page.Find("#job-invoiced-percent").GetAttribute("value").Should().Be("0", "legacy: invoicedPercent || 0");
        page.Find("#job-devEndedInDate").GetAttribute("value").Should().Be("2026-03-01");
        page.Find("#job-assemblyEndedInDate").GetAttribute("value").Should().BeEmpty();

        var header = Scenario("hours and costs registered").GetProperty("page").GetProperty("header");
        Text(page, "job-tasks-total").Should().Be(header.GetProperty("tasksTotal").GetString());
        Text(page, "job-budget-total").Should().Be(header.GetProperty("budgetTotal").GetString());
        Text(page, "job-registered").Should().Be(header.GetProperty("registered").GetString());
        Text(page, "job-deviation").Should().Be(header.GetProperty("deviation").GetProperty("text").GetString());
        page.Find("[data-testid=job-deviation]").ClassList.Should().Contain(header.GetProperty("deviation").GetProperty("class").GetString());
        Text(page, "job-total-profit").Should().Be(header.GetProperty("totalProfit").GetProperty("text").GetString());
        Text(page, "job-total-percent").Should().Be(header.GetProperty("totalPercent").GetProperty("text").GetString());
        var rows = Scenario("hours and costs registered").GetProperty("page").GetProperty("rows");
        page.Find("[data-testid=job-row-development]").TextContent.Should().Contain(rows[0].GetProperty("budgeted").GetString())
            .And.Contain(rows[0].GetProperty("deviation").GetString()).And.Contain(rows[0].GetProperty("percent").GetProperty("text").GetString());
        page.Find("#job-register-development").GetAttribute("value").Should().Be("22");
        page.Find("#job-register-materials").GetAttribute("value").Should().Be("120");
        page.Find("[data-testid=job-machining-validated-by]").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task Typing_a_value_updates_the_costs_at_once_and_blur_registers_it()
    {
        _jobs.RegisterValueAsync(JobId, Arg.Any<object?>(), Arg.Any<string>()).Returns(ApiResult.Success(Job(JobStates.Machining)));
        var page = await RenderAsync();

        await page.Find("#job-register-machining").InputAsync(new ChangeEventArgs { Value = "14.5" });

        var header = Scenario("hours typed as texts").GetProperty("page").GetProperty("header");
        page.WaitForAssertion(() => Text(page, "job-registered").Should().Be(header.GetProperty("registered").GetString()));
        Text(page, "job-deviation").Should().Be(header.GetProperty("deviation").GetProperty("text").GetString());
        await _jobs.DidNotReceiveWithAnyArgs().RegisterValueAsync(default!, default, default!);

        await page.Find("#job-register-machining").BlurAsync(new FocusEventArgs());
        await page.Find("#job-register-development").BlurAsync(new FocusEventArgs());

        await _jobs.Received(1).RegisterValueAsync(JobId, "14.5", TaskTypes.Machining);
        await _jobs.DidNotReceive().RegisterValueAsync(JobId, Arg.Any<object?>(), TaskTypes.Development); // unchanged: not sent
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Registo atualizado");
    }

    [Fact]
    public async Task A_refused_value_shows_the_servers_text()
    {
        _jobs.RegisterValueAsync(JobId, "", "custoNaoQualidade").Returns(ApiResult.Failure<JobResponse<BudgetResponse>>(HttpStatusCode.BadRequest, "Obra não existe"));
        var page = await RenderAsync();

        await page.Find("#job-register-custoNaoQualidade").InputAsync(new ChangeEventArgs { Value = "" });
        await page.Find("#job-register-custoNaoQualidade").BlurAsync(new FocusEventArgs());

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Obra não existe" && s.Severity == Severity.Error);
    }

    [Fact]
    public async Task The_delivery_date_is_refused_outside_assembly_and_billing_and_saved_in_them()
    {
        var page = await RenderAsync();
        await page.Find("#job-delivery-date").ChangeAsync(new ChangeEventArgs { Value = "2030-03-02" });
        await page.Find("#job-delivery-date").BlurAsync(new FocusEventArgs());

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Não pode alterar a data de entrega com a obra no corrente estado" && s.Severity == Severity.Warning);
        await _jobs.DidNotReceiveWithAnyArgs().ChangeSinglePropertyAsync(default!, default!, default);

        _jobs.GetByIdAsync(JobId).Returns(ApiResult.Success(Job(JobStates.Assembly)));
        var saved = Job(JobStates.Assembly) with { DeliveryDate = Stored<DateTime>.Of(new DateTime(2030, 3, 2, 0, 0, 0, DateTimeKind.Utc)) };
        _jobs.ChangeSinglePropertyAsync(JobId, "deliveryDate", "2030-03-02").Returns(ApiResult.Success(saved));
        var assembly = await RenderAsync();
        await assembly.Find("#job-delivery-date").ChangeAsync(new ChangeEventArgs { Value = "2030-03-02" });
        await assembly.Find("#job-delivery-date").BlurAsync(new FocusEventArgs());

        await _jobs.Received(1).ChangeSinglePropertyAsync(JobId, "deliveryDate", "2030-03-02");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Obra atualizada com sucesso!");
        assembly.Find("#job-delivery-date").GetAttribute("value").Should().Be("2030-03-02");
    }

    [Fact]
    public async Task Order_identification_official_flag_billed_percent_and_observations_are_single_properties()
    {
        _jobs.ChangeSinglePropertyAsync(JobId, Arg.Any<string>(), Arg.Any<object?>()).Returns(ApiResult.Success(Job(JobStates.Machining)));
        var page = await RenderAsync();

        await page.Find("#job-order-identification").InputAsync(new ChangeEventArgs { Value = "ENC-2030/002" });
        await page.Find("#job-order-identification").BlurAsync(new FocusEventArgs());
        await page.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await page.Find("#job-invoiced-percent").InputAsync(new ChangeEventArgs { Value = "50" });
        await page.Find("#job-invoiced-percent").BlurAsync(new FocusEventArgs());
        await page.Find("#job-comments").InputAsync(new ChangeEventArgs { Value = "Cliente pediu alteração da cor." });
        await page.Find("#job-comments").BlurAsync(new FocusEventArgs());

        Received.InOrder(() =>
        {
            _jobs.ChangeSinglePropertyAsync(JobId, "orderIdentification", "ENC-2030/002");
            _jobs.ChangeSinglePropertyAsync(JobId, "isOfficialOrderIdentification", true);
            _jobs.ChangeSinglePropertyAsync(JobId, "invoicedPercent", "50");
            _jobs.ChangeSinglePropertyAsync(JobId, "comments", "Cliente pediu alteração da cor.");
        });
    }

    [Fact]
    public async Task A_state_date_is_saved_when_changed_and_a_failure_is_the_legacy_text()
    {
        _jobs.ChangeStatisticsDateAsync(JobId, "approvedEndedInDate", "2026-03-10").Returns(ApiResult.Success(Job(JobStates.Machining)));
        _jobs.ChangeStatisticsDateAsync(JobId, "assemblyEndedInDate", "2026-03-11").Returns(ApiResult.Failure<JobResponse<BudgetResponse>>(HttpStatusCode.BadRequest, "x"));
        var page = await RenderAsync();

        await page.Find("#job-devEndedInDate").BlurAsync(new FocusEventArgs()); // unchanged
        await page.Find("#job-approvedEndedInDate").ChangeAsync(new ChangeEventArgs { Value = "2026-03-10" });
        await page.Find("#job-approvedEndedInDate").BlurAsync(new FocusEventArgs());
        await page.Find("#job-assemblyEndedInDate").ChangeAsync(new ChangeEventArgs { Value = "2026-03-11" });
        await page.Find("#job-assemblyEndedInDate").BlurAsync(new FocusEventArgs());

        await _jobs.Received(2).ChangeStatisticsDateAsync(JobId, Arg.Any<string>(), Arg.Any<string>());
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Data atualizada").And.Contain(s => s.Message == "Algo deu errado");
        page.Find("#job-assemblyEndedInDate").GetAttribute("value").Should().BeEmpty("a refused date shows the job's again");
    }

    [Fact]
    public async Task A_check_sends_the_statistic_with_the_answer_and_shows_the_verifier()
    {
        IReadOnlyDictionary<string, object?>? sent = null;
        var answered = Job(JobStates.Machining) with
        {
            MachiningStatistic = new JobMachiningStatisticResponse(Stored<double>.Of(14), 15, 15, "Ok", "Nok", "n/a", "m4", "Ana Administradora"),
        };
        _jobs.UpdateVerificationsAsync(JobId, Arg.Do<IReadOnlyDictionary<string, object?>>(b => sent = b)).Returns(ApiResult.Success(answered));
        var page = await RenderAsync();

        await page.FindAll("section[aria-label='Controlo de produto'] input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = true }); // its OK

        sent.Should().NotBeNull();
        sent!["updateMachiningVerificatons"].Should().Be(true);
        var machining = (Dictionary<string, object?>)sent["machiningStatistic"]!;
        machining["verificationAndShapeControl"].Should().Be("Ok");
        machining["dimensionalVerificationAndControl"].Should().Be("Nok");
        machining["_id"].Should().Be("m4");
        machining["numberOfHoursUsed"].Should().Be(14d);
        ((Dictionary<string, object?>)sent["assemblyStatistic"]!)["_id"].Should().Be("a4");
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Verificações atualizadas");
        page.WaitForAssertion(() => page.Find("[data-testid=job-machining-validated-by]").TextContent.Should().Be("Ana Administradora"));
    }

    [Fact]
    public async Task A_job_without_statistics_opens_with_zero_hours()
    {
        _jobs.GetByIdAsync(JobId).Returns(ApiResult.Success(Job(JobStates.Development) with { DevStatistic = null, MachiningStatistic = null, AssemblyStatistic = null }));
        var page = await RenderAsync();

        page.Find("#job-register-development").GetAttribute("value").Should().BeEmpty();
        Text(page, "job-registered").Should().Be("2135 €", "0 hours: 900 + 1150 + 35 + 50 (legacy: the page crashes)");
    }

    [Fact]
    public async Task An_unknown_job_shows_the_legacy_text()
    {
        _jobs.GetByIdAsync("x").Returns(ApiResult.Failure<JobResponse<BudgetResponse>>(HttpStatusCode.OK, "empty"));
        await _context.Session.GetAuthenticationStateAsync();
        _context.Render<MudSnackbarProvider>();
        var page = _context.Render<Dib4t.Client.Pages.Landing.JobDetails>(p => p.Add(c => c.JobId, "x").Add(c => c.Zone, Lisbon));

        page.WaitForAssertion(() => page.Markup.Should().Contain("Obra não existe"));
    }

    private static string Text(IRenderedComponent<Dib4t.Client.Pages.Landing.JobDetails> page, string id) => page.Find($"[data-testid={id}]").TextContent;

    private static JsonElement Scenario(string name) =>
        Golden.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private bool _providers;

    private async Task<IRenderedComponent<Dib4t.Client.Pages.Landing.JobDetails>> RenderAsync()
    {
        if (!_providers)
        {
            _context.Render<MudPopoverProvider>();
            _context.Render<MudSnackbarProvider>();
            _providers = true;
        }

        await _context.Session.GetAuthenticationStateAsync();
        var page = _context.Render<Dib4t.Client.Pages.Landing.JobDetails>(p => p.Add(c => c.JobId, JobId).Add(c => c.Zone, Lisbon));
        page.WaitForAssertion(() => page.FindAll(".job-cost-row").Should().NotBeEmpty());
        return page;
    }

    /// <summary>The "hours and costs registered" scenario of the golden file: 22 / 14 / 12 hours, 1150, 900, 120, 35, 50 €.</summary>
    private static JobResponse<BudgetResponse> Job(string state) =>
        new(Stored<double>.Of(0), 15000.5, state, "ENC-2030/001", Stored<bool>.Of(false), Stored<double>.Of(1150), Stored<double>.Of(900), Stored<double>.Of(120),
            Stored<double>.Of(35), Stored<double>.Of(50), [], JobId,
            new BudgetResponse(0, true, true, 35, 45, 30, true, BudgetId, Metal, "Máquina de corte", "Eng.ª Maria", new BudgetAuthorResponse([], "Montagem", "u1", "Ana Administradora", null, 0),
                "26-0004", "30 dias", 4500, null, new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0),
            "Ana Administradora", new DateTime(2030, 1, 25, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc),
            new JobHoursResponse(Stored<double>.Of(22), 20.5, 20.5, "d4"),
            new JobMachiningStatisticResponse(Stored<double>.Of(14), 15, 15, "n/a", "Nok", "n/a", "m4", null),
            new JobAssemblyStatisticResponse(Stored<double>.Of(12), 10, 10, "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "1", "a4", null),
            JobPriorities.Medium, 0)
        {
            JobStatisticsDates = new JobStatisticsDatesResponse("s4", Stored<DateTime>.Of(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)),
                Stored<DateTime>.Of(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc)), Stored<DateTime>.Missing, Stored<DateTime>.Missing,
                Stored<DateTime>.Missing, Stored<DateTime>.Missing),
        };
}
