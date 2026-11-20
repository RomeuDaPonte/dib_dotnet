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
using Dib4t.Domain.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// <c>POST print/:budgetId</c> from the legacy code's own answers (<c>job-print-golden.json</c>): the Joi texts, which
/// templates the original body selects (JavaScript truthiness after Joi's check), and the service: the legacy order of
/// the checks, the labels per template in order, what legacy throws on before the first label (500, nothing sent),
/// D23 and the log events. (Answers and wire bytes are checked over HTTP and TCP in
/// <c>Dib4t.Server.Tests/Jobs/JobPrintGoldenTests</c>.)
/// </summary>
public sealed class JobPrintRulesTests
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810001";
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";
    private const string JobId = "5f1e9a2b3c4d5e6f70860001";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobPrintGolden));

    private readonly IBudgetRepository _budgets = Substitute.For<IBudgetRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IPrinterSettingsRepository _printers = Substitute.For<IPrinterSettingsRepository>();
    private readonly ILabelPrinter _labels = Substitute.For<ILabelPrinter>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 15, 10, 20, 30, 123, TimeSpan.Zero));
    private readonly RecordingLogger<JobsService> _logger = new();
    private readonly List<string> _sent = [];

    public JobPrintRulesTests()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Budget());
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new BusinessEntity(ClientId, "Metalúrgica, Lda.", "509", "Rua", "4000", "Porto", "30 dias", "Cliente", 0));
        _jobs.FindForLabelsAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(new JobForLabels(JobId, Stored<DateTime>.Of(new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc))));
        _printers.ListByTemplateAsync("producao", Arg.Any<CancellationToken>()).Returns([Printer("p1", "producao"), Printer("p2", "producao")]);
        _printers.ListByTemplateAsync("maquinacao", Arg.Any<CancellationToken>()).Returns([Printer("m1", "maquinacao")]);
        _labels.PrintAsync(Arg.Any<PrinterSettings>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _sent.Add(call.Arg<PrinterSettings>().Id + " " + call.Arg<string>());
            return call.Arg<PrinterSettings>().Id != "p1"; // a failing printer: the others still print (legacy ignores the result)
        });
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private JobsService Service => new(_budgets, Substitute.For<IBudgetTaskRepository>(), _entities, _jobs, _printers, Substitute.For<INasFileShare>(), _labels,
        _time, Options.Create(new AppOptions()), _logger);

    /// <summary>The Production cases the legacy code answered with a Joi text.</summary>
    public static TheoryData<int> JoiCases => Indexes(o => o.GetProperty("status").GetInt32() == 400 && o.GetProperty("text").GetString()!.StartsWith('"'));

    /// <summary>The Production cases of the validation section that printed (selection by the original body).</summary>
    public static TheoryData<int> PrintedValidationCases => Indexes(o => o.GetProperty("status").GetInt32() == 200, "validation");

    [Theory]
    [MemberData(nameof(JoiCases))]
    public void Joi_refuses_with_the_legacy_text(int index)
    {
        var item = Golden.GetProperty("cases")[index];
        var id = item.GetProperty("path").GetString()!.Split('/')[^1];
        var body = JsonElement.Parse(item.GetProperty("body").GetString()!);

        var message = !id.StartsWith("{{", StringComparison.Ordinal) && JobValidators.ValidateId("budgetId", id) is { IsSuccess: false } refused
            ? refused.Error.Message
            : JobValidators.ValidatePrint(body).Error?.Message;

        message.Should().Be(item.GetProperty("legacy").GetProperty("text").GetString(), item.GetProperty("body").GetString());
    }

    [Theory]
    [MemberData(nameof(PrintedValidationCases))]
    public void The_original_body_selects_the_templates_by_javascript_truthiness(int index)
    {
        var item = Golden.GetProperty("cases")[index];
        var payloads = item.GetProperty("legacy").GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("send").GetString()!).ToList();

        var selection = JobValidators.ValidatePrint(JsonElement.Parse(item.GetProperty("body").GetString()!)).Value;

        (selection.Production, selection.Machining, selection.NoneChosen).Should().Be(
            (payloads.Any(p => p.Contains("^TS001", StringComparison.Ordinal)), payloads.Any(p => p.Contains("^TS002", StringComparison.Ordinal)), false),
            item.GetProperty("body").GetString());
    }

    [Fact]
    public void Only_both_json_false_is_no_template()
    {
        JobValidators.ValidatePrint(JsonElement.Parse("""{"maquinacao":false,"producao":false}""")).Value.NoneChosen.Should().BeTrue();
        JobValidators.ValidatePrint(JsonElement.Parse("""{"maquinacao":"false","producao":"false"}""")).Value
            .Should().Be(new LabelSelection(Production: true, Machining: true, NoneChosen: false), "the text \"false\" is truthy in the route");
    }

    [Fact]
    public async Task Outside_production_nothing_else_is_checked()
    {
        var answer = await Service.PrintLabelsAsync("abc", JsonElement.Parse("[]"), isProduction: false, "Ana", Cancellation);

        answer.Error.Should().Be(Error.Validation(JobMessages.PrintOnlyInProduction));
        await _budgets.DidNotReceive().FindByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_checks_come_in_the_legacy_order()
    {
        var none = await Print("""{"maquinacao":false,"producao":false}""", "5f1e9a2b3c4d5e6f708100ff");
        var missing = await Print("""{"maquinacao":true,"producao":true}""", "5f1e9a2b3c4d5e6f708100ff");
        _jobs.FindForLabelsAsync(BudgetId, Arg.Any<CancellationToken>()).Returns((JobForLabels?)null);
        var noJob = await Print("""{"maquinacao":true,"producao":true}""");

        none.Error.Should().Be(Error.Validation(JobMessages.ChooseTemplate), "before the budget is looked up");
        missing.Error.Should().Be(Error.Validation(JobMessages.BudgetMissing));
        noJob.Error.Should().Be(Error.Validation(JobMessages.NoJobToPrint));
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_producao_printer_then_every_maquinacao_printer_gets_its_label()
    {
        var answer = await Print("""{"maquinacao":true,"producao":"false"}""");

        answer.Value.Should().Be("ok");
        _sent.Should().Equal(
            "p1 \n      \u001bia3,^TS00126-0001,2/2/2026,Máquina,Metalúrgica, Lda.,26-0001,\n      ",
            "p2 \n      \u001bia3,^TS00126-0001,2/2/2026,Máquina,Metalúrgica, Lda.,26-0001,\n      ",
            "m1 \u001bia3,^TS00226-0001,2/2/2026,Máquina,Metalúrgica, Lda.,");
        _logger.Entries.Should().ContainSingle(e => e.EventId == 170).Which.Message.Should().Contain("3 sent by Ana");
    }

    [Fact]
    public async Task A_template_without_printers_is_the_legacy_400_after_the_labels_before_it()
    {
        _printers.ListByTemplateAsync("maquinacao", Arg.Any<CancellationToken>()).Returns([]);

        var answer = await Print("""{"maquinacao":true,"producao":true}""");

        answer.Error.Should().Be(Error.Validation(JobMessages.NoMachiningPrinter));
        _sent.Select(s => s[..2]).Should().Equal("p1", "p2");
        _logger.Entries.Should().Contain(e => e.EventId == 171 && e.Message.Contains("(2 labels sent before)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null-start")]
    [InlineData("client-gone")]
    [InlineData("no-client")]
    public async Task What_legacy_throws_on_before_the_first_label_is_the_500_with_nothing_sent(string shape)
    {
        switch (shape)
        {
            case "null-start":
                _jobs.FindForLabelsAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(new JobForLabels(JobId, Stored<DateTime>.Null));
                break;
            case "client-gone":
                _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns((BusinessEntity?)null);
                break;
            default:
                _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Budget() with { ClientId = null });
                break;
        }

        var answer = await Print("""{"maquinacao":true,"producao":true}""");
        _printers.ListByTemplateAsync("producao", Arg.Any<CancellationToken>()).Returns([]);
        var noPrinter = await Print("""{"maquinacao":true,"producao":true}""");

        (answer.Error!.Kind, answer.Error.Message).Should().Be((ErrorKind.Failure, LegacyMessages.SomethingFailed));
        noPrinter.Error.Should().Be(Error.Validation(JobMessages.NoProductionPrinter), "the printers are looked up before the label is made");
        _sent.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.EventId == 172);
    }

    [Fact]
    public async Task D23_the_label_date_is_the_lisbon_date_and_a_missing_start_is_now()
    {
        _jobs.FindForLabelsAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(new JobForLabels(JobId, Stored<DateTime>.Of(new DateTime(2026, 7, 31, 23, 30, 0, DateTimeKind.Utc))));
        await Print("""{"maquinacao":true,"producao":false}""");
        _jobs.FindForLabelsAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(new JobForLabels(JobId, Stored<DateTime>.Missing));
        await Print("""{"maquinacao":true,"producao":false}""");

        _sent.Should().Equal(
            "m1 \u001bia3,^TS00226-0001,1/8/2026,Máquina,Metalúrgica, Lda.,",
            "m1 \u001bia3,^TS00226-0001,15/3/2026,Máquina,Metalúrgica, Lda.,");
    }

    private Task<Result<string>> Print(string body, string budgetId = BudgetId) =>
        Service.PrintLabelsAsync(budgetId, JsonElement.Parse(body), isProduction: true, "Ana", Cancellation);

    private static TheoryData<int> Indexes(Func<JsonElement, bool> where, string? section = null)
    {
        var data = new TheoryData<int>();
        var cases = Golden.GetProperty("cases");
        for (var i = 0; i < cases.GetArrayLength(); i++)
        {
            if (cases[i].GetProperty("nodeEnv").GetString() == "prod" && (section is null || cases[i].GetProperty("section").GetString() == section)
                && where(cases[i].GetProperty("legacy")))
            {
                data.Add(i);
            }
        }

        return data;
    }

    private static Budget Budget() => new(BudgetId, 0, true, true, 35, 45, 30, true, ClientId, "Máquina", "Eng.ª", "5f1e9a2b3c4d5e6f70830001", "26-0001",
        "30 dias", 1000, null, new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static PrinterSettings Printer(string id, string template) => new(id, "192.168.2.32", 9100, template, 0);
}
