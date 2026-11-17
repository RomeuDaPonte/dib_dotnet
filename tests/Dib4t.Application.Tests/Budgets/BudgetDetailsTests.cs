using System.Text;
using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Settings;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Budgets;

/// <summary>
/// The budget details contract (legacy <c>routes/task.js</c>, <c>patchProperty</c>, <c>import</c>): the Joi texts of
/// every golden case (<c>budget-details-golden.json</c>, the legacy routes' own answers), the values Mongoose then
/// stores, the order of the checks, the decided D8 and D24 (the import adds into the typed number), and the log events. (Documents and answers are checked over
/// HTTP on MongoDB in <c>Dib4t.Server.Tests</c>.)
/// </summary>
public sealed class BudgetDetailsTests
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810001";
    private const string SourceId = "5f1e9a2b3c4d5e6f70810010";
    private const string JobBudgetId = "5f1e9a2b3c4d5e6f70810013";
    private const string SourceR1Id = "5f1e9a2b3c4d5e6f70810011";
    private const string TaskId = "5f1e9a2b3c4d5e6f70840001";
    private const string Unknown = "5f1e9a2b3c4d5e6f708399ff";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.BudgetDetailsGolden));

    private static readonly HashSet<string> LookupTexts = new(StringComparer.Ordinal)
    {
        BudgetMessages.Missing, BudgetMessages.PatchMissing, BudgetMessages.NotFound, BudgetMessages.ImportSourceMissing,
        BudgetMessages.HasJob, BudgetMessages.TaskMissing, BudgetMessages.TaskNotDeleted,
    };

    private readonly IBudgetRepository _budgets = Substitute.For<IBudgetRepository>();
    private readonly IBudgetTaskRepository _tasks = Substitute.For<IBudgetTaskRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IPricesRepository _prices = Substitute.For<IPricesRepository>();
    private readonly RecordingLogger<BudgetsService> _logger = new();
    private readonly RecordingLogger<BudgetTasksService> _taskLogger = new();

    public BudgetDetailsTests()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored(BudgetId));
        _budgets.FindByIdAsync(SourceId, Arg.Any<CancellationToken>()).Returns(Stored(SourceId) with { BudgetNumber = "26-0003", Total = null, Description = null });
        _budgets.FindByIdAsync(JobBudgetId, Arg.Any<CancellationToken>()).Returns(Stored(JobBudgetId) with { HasGenerateJob = true });
        // Revisions, highest first: 26-0003 has R1 (margin 40) and R0 (SourceId); 26-0007 only BudgetId; 26-0004 has a job.
        _budgets.ListRevisionsAsync("26-0003", Arg.Any<CancellationToken>()).Returns(
            [Stored(SourceR1Id) with { BudgetNumber = "26-0003", Revision = 1, Margin = 40 }, Stored(SourceId) with { BudgetNumber = "26-0003", Total = null, Description = null }]);
        _budgets.ListRevisionsAsync("26-0007", Arg.Any<CancellationToken>()).Returns([Stored(BudgetId)]);
        _budgets.ListRevisionsAsync("26-0004", Arg.Any<CancellationToken>()).Returns([Stored(JobBudgetId) with { BudgetNumber = "26-0004", HasGenerateJob = true }]);
        _budgets.ListRevisionsAsync("00-0000", Arg.Any<CancellationToken>()).Returns([]);
        _budgets.PatchAsync(Arg.Any<string>(), Arg.Any<BudgetPatch>(), Arg.Any<CancellationToken>()).Returns(call => Stored(call.Arg<string>()));
        _budgets.ImportAsync(Arg.Any<string>(), Arg.Any<BudgetImport>(), Arg.Any<CancellationToken>()).Returns(call => Stored(call.Arg<string>()));
        _tasks.FindByIdAsync(TaskId, Arg.Any<CancellationToken>()).Returns(TaskOf(TaskId, BudgetId));
        _tasks.UpdateAsync(TaskId, Arg.Any<TaskInput>(), Arg.Any<CancellationToken>()).Returns(TaskOf(TaskId, BudgetId));
        _tasks.ListByBudgetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => [TaskOf("5f1e9a2b3c4d5e6f70840011", call.Arg<string>())]);
        _prices.FindAsync(Arg.Any<CancellationToken>()).Returns(new Prices(32.5, 10, 10, 10, 10, 35));
    }

    public static TheoryData<int> PatchCases => Indexes("patch");

    public static TheoryData<int> UpdateTaskCases => Indexes("updateTask");

    public static TheoryData<int> ImportCases => Indexes("import");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private BudgetsService Budgets => new(_budgets, _tasks, _entities, _prices, new BudgetNumbering(new RecordingLogger<BudgetNumbering>()),
        Substitute.For<Nas.INasFileShare>(), new FakeTimeProvider(), Options.Create(new AppOptions()), _logger);

    private BudgetTasksService Tasks => new(_budgets, _tasks, _prices, _taskLogger);

    [Theory]
    [MemberData(nameof(PatchCases))]
    public void Patch_bodies_are_validated_and_cast_like_the_legacy_route(int index)
    {
        var item = Golden.GetProperty("patch")[index];
        var body = JsonElement.Parse(item.GetProperty("body").GetString()!);

        var validated = BudgetDetailsValidators.ValidatePatch(body);

        if (JoiText(item) is { } text)
        {
            validated.Error.Should().Be(Error.Validation(text), item.GetProperty("body").GetString());
            return;
        }

        validated.IsSuccess.Should().BeTrue(item.GetProperty("body").GetString());
        var patch = validated.Value;
        if (item.TryGetProperty("error", out var error) && error.GetString()!.StartsWith("ValidationError", StringComparison.Ordinal))
        {
            patch.CastFailure.Should().Be(error.GetString()!.Replace("ValidationError: ", string.Empty, StringComparison.Ordinal));
            return;
        }

        patch.CastFailure.Should().BeNull();
        if (item.GetProperty("budgetId").GetString() is "{{budget}}" && item.TryGetProperty("json", out var json))
        {
            // The answer shows the value Mongoose stored (a number text as its number, a text as sent; NFC in the new app).
            var stored = json.GetProperty(patch.Field);
            switch (stored.ValueKind)
            {
                case JsonValueKind.Number:
                    patch.Number.Should().Be(stored.GetDouble());
                    break;
                case JsonValueKind.String:
                    patch.Text.Should().Be(stored.GetString()!.Normalize(NormalizationForm.FormC));
                    break;
                default:
                    patch.Flag.Should().Be(stored.GetBoolean());
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(UpdateTaskCases))]
    public void Task_bodies_and_ids_are_validated_like_the_legacy_route(int index)
    {
        var item = Golden.GetProperty("updateTask")[index];
        var body = item.GetProperty("body").GetString()!;
        var taskId = Ids(item.GetProperty("taskId").GetString()!);
        var budgetId = Ids(item.GetProperty("budgetId").GetString()!);

        var validated = BudgetDetailsValidators.ValidateTask(JsonElement.Parse(body));
        var ids = validated.IsSuccess ? BudgetDetailsValidators.ValidateTaskIds(taskId, budgetId) : null;

        if (JoiText(item) is { } text)
        {
            (validated.IsSuccess ? ids!.Error : validated.Error).Should().Be(Error.Validation(text), body);
            return;
        }

        ids!.IsSuccess.Should().BeTrue(body);
        if (item.TryGetProperty("json", out var json))
        {
            validated.Value.Should().Be(new TaskInput(
                json.GetProperty("taskType").GetString()!,
                json.TryGetProperty("description", out var description) ? description.GetString()!.Normalize(NormalizationForm.FormC) : null,
                json.GetProperty("unitPrice").GetDouble(),
                json.GetProperty("quantity").GetDouble()), body);
        }
    }

    [Theory]
    [MemberData(nameof(ImportCases))]
    public void Import_bodies_are_validated_like_the_legacy_route(int index)
    {
        var item = Golden.GetProperty("import")[index];
        var body = item.GetProperty("body").GetString()!;

        var validated = BudgetDetailsValidators.ValidateImport(JsonElement.Parse(body));

        if (JoiText(item) is { } text)
        {
            validated.Error.Should().Be(Error.Validation(text), body);
        }
        else
        {
            validated.IsSuccess.Should().BeTrue(body);
        }
    }

    [Fact]
    public void Import_flags_are_the_original_values_truthiness()
    {
        BudgetDetailsValidators.ValidateImport(JsonElement.Parse("""{"importDescription":"false","importTasks":"FALSE","budgetNumber":"26-0003"}"""))
            .Value.Should().Be(new BudgetImportInput(true, true, "26-0003"), "legacy tests `if (req.body.importTasks)` on the text");
        BudgetDetailsValidators.ValidateImport(JsonElement.Parse("""{"importDescription":false,"importTasks":true,"budgetNumber":"26-0003"}"""))
            .Value.Should().Be(new BudgetImportInput(false, true, "26-0003"));
    }

    [Fact]
    public async Task Patch_checks_joi_the_id_the_budget_the_job_then_the_cast()
    {
        var joi = await Budgets.PatchAsync("abc", Body("""{"propertyName":"x","value":1}"""), Cancellation);
        var cast = await Budgets.PatchAsync("abc", Body("""{"propertyName":"validity","value":10}"""), Cancellation);
        var missing = await Budgets.PatchAsync(Unknown, Body("""{"propertyName":"complete","value":"TRUE"}"""), Cancellation);
        var job = await Budgets.PatchAsync(JobBudgetId, Body("""{"propertyName":"complete","value":"TRUE"}"""), Cancellation);
        var refused = await Budgets.PatchAsync(BudgetId, Body("""{"propertyName":"complete","value":"TRUE"}"""), Cancellation);
        var saved = await Budgets.PatchAsync(BudgetId, Body("""{"propertyName":"total","value":"999"}"""), Cancellation);

        joi.Error.Should().Be(Error.Validation("Não é possível atualizar x"));
        cast.Error!.Message.Should().Be("Something failed.");
        missing.Error.Should().Be(Error.Validation(BudgetMessages.PatchMissing));
        job.Value.Budget.Id.Should().Be(JobBudgetId, "a budget with a job is answered unchanged (200)");
        refused.Error!.Kind.Should().Be(ErrorKind.Failure);
        saved.IsSuccess.Should().BeTrue();
        await _budgets.Received(1).PatchAsync(Arg.Any<string>(), Arg.Any<BudgetPatch>(), Arg.Any<CancellationToken>());
        await _budgets.Received().PatchAsync(BudgetId, BudgetPatch.OfNumber("total", 999), Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 80 && e.Message.Contains("total changed", StringComparison.Ordinal));
        _logger.Entries.Should().Contain(e => e.EventId == 76 && e.Message.Contains("Cast to Boolean failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D24_import_checks_joi_the_number_the_id_the_budget_itself_then_the_receiving_budgets_job()
    {
        var joi = await Budgets.ImportAsync("abc", Body("""{"importTasks":true}"""), Cancellation);
        var number = await Budgets.ImportAsync("abc", ImportBody("00-0000"), Cancellation);
        var cast = await Budgets.ImportAsync("abc", ImportBody("26-0003"), Cancellation);
        var missing = await Budgets.ImportAsync(Unknown, ImportBody("26-0003"), Cancellation);
        var itself = await Budgets.ImportAsync(BudgetId, ImportBody("26-0007"), Cancellation);
        var job = await Budgets.ImportAsync(BudgetId, ImportBody("26-0004"), Cancellation);

        joi.Error.Should().Be(Error.Validation("Erro: \"importDescription\" is required"));
        number.Error.Should().Be(Error.Validation(BudgetMessages.ImportSourceMissing));
        cast.Error!.Message.Should().Be("Something failed.");
        missing.Error.Should().Be(Error.Validation(BudgetMessages.NotFound));
        itself.Error.Should().Be(Error.Validation(BudgetMessages.ImportIntoItself));
        job.Error.Should().Be(Error.Validation(BudgetMessages.HasJob), "the receiving budget has a job");
        await _tasks.DidNotReceiveWithAnyArgs().InsertCopiesAsync(default!, default!, Cancellation);
        await _budgets.DidNotReceiveWithAnyArgs().ImportAsync(default!, default!, Cancellation);
    }

    [Fact]
    public async Task D24_import_adds_the_open_budgets_tasks_to_the_highest_revision_and_recomputes_its_total()
    {
        var result = await Budgets.ImportAsync(BudgetId, ImportBody("26-0003"), Cancellation);

        result.Value.Budget.Id.Should().Be(SourceR1Id, "the receiving budget is answered");
        Received.InOrder(() =>
        {
            _tasks.ListByBudgetAsync(BudgetId, Arg.Any<CancellationToken>());
            _tasks.InsertCopiesAsync(SourceR1Id, Arg.Is<IReadOnlyList<BudgetTask>>(t => t.Count == 1 && t[0].BudgetId == BudgetId), Arg.Any<CancellationToken>());
            _tasks.ListByBudgetAsync(SourceR1Id, Arg.Any<CancellationToken>());
            // its tasks (the substitute: one of 32.5) with its own margin 40: 32.5 + trunc(13) = 45.5; the open budget's description
            _budgets.ImportAsync(SourceR1Id, new BudgetImport(45.5, "Texto"), Arg.Any<CancellationToken>());
        });
        await _tasks.DidNotReceiveWithAnyArgs().DeleteByBudgetAsync(default!, Cancellation);
        await _tasks.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Cancellation);
        await _budgets.DidNotReceive().ImportAsync(BudgetId, Arg.Any<BudgetImport>(), Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 81 && e.Message.Contains("tasks added 1, description replaced True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D24_revision_0_can_import_into_the_latest_revision_of_its_own_number()
    {
        var result = await Budgets.ImportAsync(SourceId, ImportBody("26-0003"), Cancellation);

        result.Value.Budget.Id.Should().Be(SourceR1Id);
        await _tasks.Received().InsertCopiesAsync(SourceR1Id, Arg.Any<IReadOnlyList<BudgetTask>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task D24_without_a_description_the_receiving_budget_keeps_its_own_and_unticked_imports_change_nothing()
    {
        await Budgets.ImportAsync(SourceId, Body("""{"importDescription":true,"importTasks":false,"budgetNumber":"26-0007"}"""), Cancellation);
        await Budgets.ImportAsync(SourceId, Body("""{"importDescription":false,"importTasks":false,"budgetNumber":"26-0007"}"""), Cancellation);

        await _tasks.DidNotReceiveWithAnyArgs().ListByBudgetAsync(default!, Cancellation);
        await _budgets.Received(1).ImportAsync(BudgetId, new BudgetImport(null, null), Arg.Any<CancellationToken>()); // the flags only
    }

    [Fact]
    public async Task Task_edit_checks_the_body_the_ids_the_task_the_budget_and_the_job_then_marks_the_budget()
    {
        var body = Body("""{"taskType":"development","description":"x","unitPrice":"30","quantity":20}""");
        var joi = await Tasks.UpdateAsync("abc", "def", Body("""{"taskType":"painting"}"""), Cancellation);
        var ids = await Tasks.UpdateAsync("abc", "def", body, Cancellation);
        var task = await Tasks.UpdateAsync(Unknown, Unknown, body, Cancellation);
        var budget = await Tasks.UpdateAsync(TaskId, Unknown, body, Cancellation);
        var job = await Tasks.UpdateAsync(TaskId, JobBudgetId, body, Cancellation);
        var saved = await Tasks.UpdateAsync(TaskId.ToUpperInvariant(), BudgetId, body, Cancellation);

        joi.Error!.Message.Should().StartWith("\"taskType\" must be one of [automation,");
        ids.Error.Should().Be(Error.Validation("\"taskId\" with value \"abc\" fails to match the valid mongo id pattern"));
        task.Error.Should().Be(Error.Validation(BudgetMessages.TaskMissing));
        budget.Error.Should().Be(Error.Validation(BudgetMessages.Missing));
        job.Error.Should().Be(Error.Validation(BudgetMessages.HasJob));
        saved.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _tasks.UpdateAsync(TaskId, new TaskInput("development", "x", 30, 20), Arg.Any<CancellationToken>());
            _budgets.SetIncompleteAsync(BudgetId, Arg.Any<CancellationToken>());
        });
        await _budgets.Received(1).SetIncompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _taskLogger.Entries.Should().Contain(e => e.EventId == 91);
    }

    [Fact]
    public async Task Task_delete_marks_the_budget_before_deleting_and_a_missing_task_is_a_500()
    {
        var missing = await Tasks.DeleteAsync(Unknown, BudgetId, Cancellation);

        missing.Error.Should().Be(Error.Failure(BudgetMessages.TaskNotDeleted));
        Received.InOrder(() =>
        {
            _budgets.SetIncompleteAsync(BudgetId, Arg.Any<CancellationToken>());
            _tasks.DeleteAsync(Unknown, Arg.Any<CancellationToken>());
        });
        _taskLogger.Entries.Should().Contain(e => e.EventId == 93 && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Default_task_checks_the_id_the_budget_the_job_and_the_prices_and_leaves_the_flags()
    {
        _tasks.InsertDefaultAsync(BudgetId, 32.5, Arg.Any<CancellationToken>()).Returns(TaskOf("5f1e9a2b3c4d5e6f70840099", BudgetId));
        var cast = await Tasks.CreateDefaultAsync("abc", Cancellation);
        var missing = await Tasks.CreateDefaultAsync(Unknown, Cancellation);
        var job = await Tasks.CreateDefaultAsync(JobBudgetId, Cancellation);
        var created = await Tasks.CreateDefaultAsync(BudgetId, Cancellation);
        _prices.FindAsync(Arg.Any<CancellationToken>()).Returns((Prices?)null);
        var noPrices = await Tasks.CreateDefaultAsync(BudgetId, Cancellation);

        cast.Error!.Message.Should().Be("Something failed.");
        missing.Error.Should().Be(Error.Validation(BudgetMessages.Missing));
        job.Error.Should().Be(Error.Validation(BudgetMessages.HasJob));
        created.IsSuccess.Should().BeTrue();
        noPrices.Error!.Kind.Should().Be(ErrorKind.Failure);
        await _budgets.DidNotReceiveWithAnyArgs().SetIncompleteAsync(default!, Cancellation);
    }

    [Fact]
    public async Task D8_create_validates_the_intended_schema_then_the_budget()
    {
        var input = new TaskInput("others", "x", 1, 2);
        _tasks.InsertAsync(BudgetId, input, Arg.Any<CancellationToken>()).Returns(TaskOf("5f1e9a2b3c4d5e6f70840099", BudgetId));
        var joi = await Tasks.CreateAsync(Body("""{"taskType":"others","unitPrice":1,"quantity":2}"""), Cancellation);
        var missing = await Tasks.CreateAsync(Body($$"""{"taskType":"others","unitPrice":1,"quantity":2,"budgetId":"{{Unknown}}"}"""), Cancellation);
        var created = await Tasks.CreateAsync(Body($$"""{"budgetId":"{{BudgetId.ToUpperInvariant()}}","taskType":"others","description":"x","unitPrice":"1","quantity":2}"""), Cancellation);

        joi.Error.Should().Be(Error.Validation("\"budgetId\" is required"));
        missing.Error.Should().Be(Error.Validation(BudgetMessages.Missing));
        created.IsSuccess.Should().BeTrue();
        await _budgets.Received(1).SetIncompleteAsync(BudgetId, Arg.Any<CancellationToken>());
    }

    /// <summary>The 400 text when it comes from Joi (the route's first check), else <c>null</c>.</summary>
    private static string? JoiText(JsonElement item) =>
        item.GetProperty("status").GetInt32() == 400 && item.GetProperty("text").GetString() is { } text && !LookupTexts.Contains(text) ? text : null;

    private static string Ids(string text) =>
        text.StartsWith("{{", StringComparison.Ordinal) ? Golden.GetProperty("ids").GetProperty(text).GetString()! : text;

    private static TheoryData<int> Indexes(string section) => [.. Enumerable.Range(0, Golden.GetProperty(section).GetArrayLength())];

    private static JsonElement Body(string json) => JsonElement.Parse(json);

    private static JsonElement ImportBody(string number) =>
        Body($$"""{"importDescription":true,"importTasks":true,"budgetNumber":"{{number}}"}""");

    private static Budget Stored(string id) => new(id, 0, true, true, 35, 60, 30, false, "5f1e9a2b3c4d5e6f70820001", "Máquina de corte", "Eng.ª Maria",
        "5f1e9a2b3c4d5e6f70830001", "26-0007", "30 dias", 43.5, "Texto", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static BudgetTask TaskOf(string id, string budgetId) => new(id, budgetId, "automation", string.Empty, 32.5, 1, 0);
}
