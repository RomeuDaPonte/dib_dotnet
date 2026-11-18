using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Application.Jobs;
using Dib4t.Application.Nas;
using Dib4t.Application.Printing;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;
using Dib4t.Domain.Jobs;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// Legacy <c>POST /api/job</c> (<c>routes/job.js</c>): the order of the checks and their texts, what is written and
/// in which order, D26 (nothing written when the legacy save would fail after marking the budget), the NAS step (D3,
/// D19: the folder of revision 0; the job folder under the job's revision; years in the configured zone; a failure →
/// 500 after the job is saved, no labels) and the labels (producao then maquinacao, the start date in the zone).
/// </summary>
public sealed class JobsServiceTests
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810002";
    private const string RevisionZeroId = "5f1e9a2b3c4d5e6f70810001";
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";
    private const string UserId = "5f1e9a2b3c4d5e6f70830001";

    private static readonly BusinessEntity Client = new(ClientId, "Metalúrgica", "509", "Rua", "4000", "Porto", "30 dias", "Cliente", 0);
    private static readonly BudgetAuthor Author = new(UserId, "Ana", "ana@dib4t.local", ["canSeeOrcamentos"], "Desenvolvimento", 0);

    private readonly IBudgetRepository _budgets = Substitute.For<IBudgetRepository>();
    private readonly IBudgetTaskRepository _tasks = Substitute.For<IBudgetTaskRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IPrinterSettingsRepository _printers = Substitute.For<IPrinterSettingsRepository>();
    private readonly INasFileShare _nas = Substitute.For<INasFileShare>();
    private readonly ILabelPrinter _labels = Substitute.For<ILabelPrinter>();

    // 2026-12-31 23:30 UTC = 2026-12-31 23:30 in Lisbon (winter); the revision below is from 2026-07-31 23:30 UTC = 1 August in Lisbon.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 31, 23, 30, 0, 456, TimeSpan.Zero).AddTicks(789));
    private readonly RecordingLogger<JobsService> _logger = new();
    private readonly List<string> _calls = [];

    public JobsServiceTests()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Revision1());
        _budgets.FindRevisionZeroAsync("25-0042", Arg.Any<CancellationToken>()).Returns(Revision0());
        _budgets.FindAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns(Author);
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(Client);
        _tasks.ListByBudgetAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(
        [
            Task("development", 20), Task("development", 0.5), Task("machining", 15), Task("assembly", 10), Task("consultancy", 3),
        ]);
        _budgets.SetHasGenerateJobAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("budget");
            return Revision1() with { HasGenerateJob = true };
        });
        _jobs.InsertAsync(Arg.Any<NewJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add("job");
            return Inserted(call.Arg<NewJob>());
        });
        _nas.MoveBudgetFolderToJobAsync(Arg.Any<BudgetFolder>(), Arg.Any<JobFolder>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("nas");
            return NasResult.Done;
        });
        _printers.ListByTemplateAsync("producao", Arg.Any<CancellationToken>()).Returns([Printer("p1", "producao"), Printer("p2", "producao")]);
        _printers.ListByTemplateAsync("maquinacao", Arg.Any<CancellationToken>()).Returns([Printer("m1", "maquinacao")]);
        _labels.PrintAsync(Arg.Any<PrinterSettings>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add("label " + call.Arg<PrinterSettings>().Id);
            return false; // legacy ignores the result
        });
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private JobsService Service => new(_budgets, _tasks, _entities, _jobs, _printers, _nas, _labels, _time, Options.Create(new AppOptions()), _logger);

    [Fact]
    public async Task The_checks_come_in_the_legacy_order_and_write_nothing()
    {
        var joi = await Service.CreateAsync(Body(finalPrice: 0), "Ana", Cancellation);
        var missing = await Service.CreateAsync(Body(budgetId: "5f1e9a2b3c4d5e6f708100ff"), "Ana", Cancellation);
        _budgets.FindWithJobAsync("25-0042", Arg.Any<CancellationToken>()).Returns(Revision0() with { HasGenerateJob = true });
        var already = await Service.CreateAsync(Body(isOfficial: "TRUE"), "Ana", Cancellation);
        _budgets.FindWithJobAsync("25-0042", Arg.Any<CancellationToken>()).Returns((Budget?)null);
        _budgets.FindRevisionZeroAsync("25-0042", Arg.Any<CancellationToken>()).Returns((Budget?)null);
        var noRevisionZero = await Service.CreateAsync(Body(isOfficial: "TRUE"), "Ana", Cancellation);

        joi.Error.Should().Be(Error.Validation("\"finalPrice\" must be larger than or equal to 1"));
        missing.Error.Should().Be(Error.Validation(JobMessages.BudgetMissing));
        already.Error.Should().Be(Error.Validation(JobMessages.AlreadyCreated), "before the casts of the save (D26 keeps the legacy order)");
        noRevisionZero.Error.Should().Be(Error.Validation(JobMessages.RevisionZeroMissing));
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("client")]
    [InlineData("author")]
    [InlineData("paymentMethod")]
    public async Task A_budget_the_legacy_save_refuses_is_a_500_with_nothing_written(string missing)
    {
        switch (missing)
        {
            case "client":
                _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns((BusinessEntity?)null);
                break;
            case "author":
                _budgets.FindAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns((BudgetAuthor?)null);
                break;
            default:
                _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Revision1() with { PaymentMethod = "" });
                break;
        }

        var created = await Service.CreateAsync(Body(), "Ana", Cancellation);

        created.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        created.Error.Reason.Should().StartWith("Budget validation failed");
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task D26_a_value_the_legacy_save_refuses_writes_nothing()
    {
        var flag = await Service.CreateAsync(Body(isOfficial: "TRUE"), "Ana", Cancellation);
        _tasks.ListByBudgetAsync(BudgetId, Arg.Any<CancellationToken>()).Returns([Task("machining", null)]);
        var hours = await Service.CreateAsync(Body(), "Ana", Cancellation);

        flag.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        flag.Error.Reason.Should().Contain("D26").And.Contain("Cast to Boolean");
        hours.Error!.Reason.Should().Contain("NaN");
        _calls.Should().BeEmpty("legacy marks the budget and then fails; the number could never get its job");
        _logger.Entries.Should().Contain(e => e.EventId == 152 && e.Message.Contains("D26", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_marks_the_budget_saves_the_job_moves_the_folders_and_prints_in_the_legacy_order()
    {
        var created = await Service.CreateAsync(Body(), "Ana Administradora", Cancellation);

        created.IsSuccess.Should().BeTrue(created.Error?.Reason);
        _calls.Should().Equal("budget", "job", "nas", "label p1", "label p2", "label m1");
        await _jobs.Received(1).InsertAsync(
            new NewJob(BudgetId, 15000.5, new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc), "Em Aprovação", "PO-1", true, "Ana Administradora",
                new DateTime(2026, 12, 31, 23, 30, 0, 456, DateTimeKind.Utc), 20.5, 15, 10),
            Arg.Any<CancellationToken>());
        created.Value.Budget.Budget.HasGenerateJob.Should().BeTrue();
        (created.Value.Budget.Client, created.Value.Budget.MadeBy).Should().Be((Client, Author));
        _logger.Entries.Should().Contain(e => e.EventId == 150 && e.Message.Contains("25-0042 R1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D3_D19_the_budget_folder_is_revision_0s_and_the_job_folder_the_revisions_in_the_zone()
    {
        await Service.CreateAsync(Body(), "Ana", Cancellation);

        // Revision 0: 2025-12-31 23:30 UTC (2025 in Lisbon), "Máquina"; revision 1: 2026-07-31 23:30 UTC, "Máquina (rev. 1)".
        await _nas.Received(1).MoveBudgetFolderToJobAsync(
            new BudgetFolder(2025, "Metalúrgica", "25-0042", "Máquina"),
            new JobFolder(2026, "Metalúrgica", "25-0042", "Máquina (rev. 1)"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_labels_carry_the_start_date_in_the_zone_and_the_revisions_descriptive()
    {
        _time.AdjustTime(new DateTimeOffset(2026, 7, 31, 23, 30, 0, TimeSpan.Zero)); // 1 August in Lisbon

        await Service.CreateAsync(Body(), "Ana", Cancellation);

        await _labels.Received(1).PrintAsync(Arg.Is<PrinterSettings>(p => p.Id == "p1"),
            "\n      \u001bia3,^TS00125-0042,1/8/2026,Máquina (rev. 1),Metalúrgica,25-0042,\n      ", Arg.Any<CancellationToken>());
        await _labels.Received(1).PrintAsync(Arg.Is<PrinterSettings>(p => p.Id == "m1"),
            "\u001bia3,^TS00225-0042,1/8/2026,Máquina (rev. 1),Metalúrgica,", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(NasResult.Failed)]
    [InlineData(NasResult.FolderMissing)]
    public async Task A_failed_move_is_the_legacy_500_after_the_job_is_saved_and_nothing_is_printed(NasResult result)
    {
        _nas.MoveBudgetFolderToJobAsync(Arg.Any<BudgetFolder>(), Arg.Any<JobFolder>(), Arg.Any<CancellationToken>()).Returns(result);

        var created = await Service.CreateAsync(Body(), "Ana", Cancellation);

        created.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        _calls.Should().Equal("budget", "job");
        await _labels.DidNotReceive().PrintAsync(Arg.Any<PrinterSettings>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 153);
    }

    [Fact]
    public async Task A_budget_deleted_meanwhile_is_not_found()
    {
        _budgets.SetHasGenerateJobAsync(BudgetId, Arg.Any<CancellationToken>()).Returns((Budget?)null);

        var created = await Service.CreateAsync(Body(), "Ana", Cancellation);

        created.Error.Should().Be(Error.Validation(JobMessages.BudgetMissing));
        await _jobs.DidNotReceive().InsertAsync(Arg.Any<NewJob>(), Arg.Any<CancellationToken>());
    }

    private static JsonElement Body(string budgetId = BudgetId, double finalPrice = 15000.5, string? isOfficial = null) => JsonElement.Parse(
        $$"""
        {"budgetId":"{{budgetId.ToUpperInvariant()}}","finalPrice":{{finalPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "dueDate":"2030-01-15","state":"Em Aprovação","orderIdentification":"PO-1","isOfficialOrderIdentification":{{(isOfficial is null ? "true" : $"\"{isOfficial}\"")}}}
        """);

    private static Budget Revision0() => new(RevisionZeroId, 0, true, true, 35, 45, 30, false, ClientId, "Máquina", "Eng.ª", UserId, "25-0042",
        "30 dias", 1000, null, new DateTime(2025, 12, 31, 23, 30, 0, DateTimeKind.Utc), 0);

    private static Budget Revision1() => Revision0() with
    {
        Id = BudgetId,
        Revision = 1,
        Descriptive = "Máquina (rev. 1)",
        Date = new DateTime(2026, 7, 31, 23, 30, 0, DateTimeKind.Utc),
    };

    private static BudgetTask Task(string type, double? quantity) => new(Guid.NewGuid().ToString("N")[..24], BudgetId, type, "", 30, quantity, 0);

    private static PrinterSettings Printer(string id, string template) => new(id, "192.168.2.32", 9100, template, 0);

    private static Job Inserted(NewJob job) => new("5f1e9a2b3c4d5e6f708500aa", 0, job.FinalPrice, job.State, job.OrderIdentification,
        job.IsOfficialOrderIdentification, 0, 0, 0, 0, 0, [], job.BudgetId, job.CreatedBy, job.DueDate, job.StartDate,
        new JobHours(0, job.DevelopmentHours, job.DevelopmentHours, "a"), null, null, "Média", 0);
}
