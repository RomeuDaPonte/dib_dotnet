using System.Text.Json;
using Dib4t.Application.Jobs;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// The jobs board's rules from the legacy code's own answers (<c>job-board-golden.json</c>): the Joi 400 texts of the
/// statistics schemas (per <b>current</b> state), the state and priority bodies and the ids; plus the job model's
/// methods (<see cref="JobChanges"/>) and the board ordering. (Answers and documents are checked on MongoDB over HTTP
/// in <c>Dib4t.Server.Tests/Jobs/JobBoardGoldenTests</c>.)
/// </summary>
public sealed class JobBoardRulesTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobBoardGolden));

    private static readonly DateTime Now = new(2026, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc);

    /// <summary>The golden cases answered with a Joi text (a 400 whose text starts with a quoted label).</summary>
    public static TheoryData<int> JoiCases
    {
        get
        {
            var data = new TheoryData<int>();
            var cases = Golden.GetProperty("cases");
            for (var i = 0; i < cases.GetArrayLength(); i++)
            {
                var outcome = cases[i].TryGetProperty("fixed", out var fixedOutcome) ? fixedOutcome : cases[i];
                if (outcome.GetProperty("status").GetInt32() == 400 && outcome.GetProperty("text").GetString()!.StartsWith('"'))
                {
                    data.Add(i);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(JoiCases))]
    public void Joi_refuses_with_the_legacy_text(int index)
    {
        var item = Golden.GetProperty("cases")[index];
        var outcome = item.TryGetProperty("fixed", out var fixedOutcome) ? fixedOutcome : item;
        var segments = item.GetProperty("path").GetString()!.Split('/');
        var section = item.GetProperty("section").GetString();
        var body = item.GetProperty("body").ValueKind == JsonValueKind.Null ? JsonElement.Parse("{}") : JsonElement.Parse(item.GetProperty("body").GetString()!);
        var last = segments[^1];

        var message = section switch
        {
            "get" when segments[3] == "getByState" => JobValidators.ValidateState(JsonSerializer.SerializeToElement(new { state = last })).Error?.Message,
            "get" => JobValidators.ValidateId(segments[3] == "getByBudgetId" ? "budgetId" : "jobId", last).Error?.Message,
            _ when !last.StartsWith("{{", StringComparison.Ordinal) => JobValidators.ValidateId("jobId", last).Error?.Message,
            "statistics" => JobValidators.ValidateStatistics(body, StateOf(last)),
            "state" => JobValidators.ValidateState(body).Error?.Message,
            "priority" => JobValidators.ValidatePriority(body).Error?.Message,
            _ => null,
        };

        message.Should().Be(outcome.GetProperty("text").GetString(), item.GetProperty("path").GetString() + " " + body.GetRawText());
    }

    [Fact]
    public void Every_kind_of_Joi_refusal_occurs()
    {
        JoiCases.Count.Should().BeGreaterThan(50);
    }

    [Fact]
    public void The_deviation_is_checked_with_JavaScript_arithmetic_and_loose_equality()
    {
        BudgetTask[] tasks = [Task(20), Task(0.5)];
        JobChanges.ConfirmDeviation(tasks, Json("\"18\""), Json("2.5")).Should().BeTrue();
        JobChanges.ConfirmDeviation(tasks, Json("18"), Json("\"2.5\"")).Should().BeTrue("2.5 == \"2.5\"");
        JobChanges.ConfirmDeviation(tasks, Json("18"), Json("5")).Should().BeFalse();
        JobChanges.ConfirmDeviation(tasks, null, Json("20.5")).Should().BeFalse("20.5 - undefined is NaN");
        JobChanges.ConfirmDeviation(tasks, Json("18"), null).Should().BeFalse();
        JobChanges.ConfirmDeviation([], Json("18"), Json("999")).Should().BeTrue("no tasks of the type: accepted");
        JobChanges.ConfirmDeviation([Task(0.1), Task(0.2)], Json("0"), Json("0.3")).Should().BeFalse("0.1 + 0.2 is 0.30000000000000004");
    }

    [Fact]
    public void Billing_is_refused_below_100_percent_unless_the_percentage_is_missing()
    {
        Changes(Job(JobStates.ForInvoicing, Stored<double>.Of(80))).ChangeState(Json("\"Faturada\"")).Should().BeFalse();
        Changes(Job(JobStates.ForInvoicing, Stored<double>.Null)).ChangeState(Json("\"Faturada\"")).Should().BeFalse("null < 100");
        Changes(Job(JobStates.ForInvoicing, Stored<double>.Missing)).ChangeState(Json("\"Faturada\"")).Should().BeTrue();
        Changes(Job(JobStates.ForInvoicing, Stored<double>.Of(100))).ChangeState(Json("\"Faturada\"")).Should().BeTrue();
        Changes(Job(JobStates.Development, Stored<double>.Of(0))).ChangeState(Json("\"Apagada\"")).Should().BeTrue();
    }

    [Fact]
    public void An_invalid_or_missing_state_is_what_the_save_refuses()
    {
        var invalid = Changes(Job(JobStates.Billed, Stored<double>.Of(100)));
        invalid.ChangeState(Json("\"Outro\""));
        invalid.Refusal.Should().Contain("is not a valid enum value");
        var missing = Changes(Job(JobStates.Billed, Stored<double>.Of(100)));
        missing.ChangeState(null);
        missing.Refusal.Should().Contain("required");
    }

    [Fact]
    public void A_new_dates_document_gets_the_end_dates_in_the_legacy_order()
    {
        var changes = Changes(Job(JobStates.Machining, Stored<double>.Of(0)));
        changes.SetStatistics(JsonElement.Parse("""{"commentType":[],"materials":"300","numberOfHoursUsed":"14","budgetedHours":15,"deviation":1,"invoicedPercent":30}"""), "Ana");
        changes.UpdateStateDates(Json("\"Montagem\""));

        var dates = changes.Assignments.Single(a => a.Path == "jobStatisticsDates").Value.Should().BeOfType<StoredValue.DocumentValue>().Subject;
        dates.Fields.Select(f => f.Key).Should().Equal("_id", "machiningEndedInDate", "onApprovalEndedInDate", "approvedEndedInDate");
        var statistic = changes.Assignments.Single(a => a.Path == "machiningStatistic").Value.Should().BeOfType<StoredValue.DocumentValue>().Subject;
        statistic.Fields.Select(f => f.Key).Should().Equal("numberOfHoursUsed", "budgetedHours", "deviation", "verificationAndShapeControl",
            "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish", "_id", "validatedBy");
        statistic.Fields[0].Value.Should().Be(StoredValue.Number(14), "\"14\" as Mongoose casts it");
    }

    [Fact]
    public void Comment_types_are_spread_like_JavaScript()
    {
        var text = Changes(Job(JobStates.Billed, Stored<double>.Of(100)));
        text.SetStatistics(JsonElement.Parse("""{"commentType":"[]"}"""), "Ana");
        text.Assignments.Single(a => a.Path == "commentType").Value.Should().BeOfType<StoredValue.TextsValue>().Which.Values.Should().Equal("[", "]");

        var missing = Changes(Job(JobStates.Billed, Stored<double>.Of(100)));
        var act = () => missing.SetStatistics(JsonElement.Parse("{}"), "Ana");
        act.Should().Throw<JobScriptException>().WithMessage("TypeError: newStatistics.commentType is not iterable");
    }

    [Fact]
    public void The_board_orders_high_then_medium_then_the_rest()
    {
        var ordered = JobBoard.ByPriority<string?>(["Baixa", null, "Alta", "Média", "Alta"], p => p);
        ordered.Should().Equal("Alta", "Alta", "Média", "Baixa", null);
    }

    [Fact]
    public void The_task_type_of_each_state()
    {
        JobChanges.TaskTypeOf(JobStates.Approved).Should().Be(TaskTypes.Development);
        JobChanges.TaskTypeOf(JobStates.Machining).Should().Be(TaskTypes.Machining);
        JobChanges.TaskTypeOf(JobStates.Assembly).Should().Be(TaskTypes.Assembly);
        JobChanges.TaskTypeOf(JobStates.ForInvoicing).Should().BeEmpty();
    }

    private static string StateOf(string jobName) =>
        Golden.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("_id").GetProperty("$oid").GetString() == jobName).GetProperty("state").GetString()!;

    private static JobChanges Changes(Job job) => new(job, Now);

    private static Job Job(string state, Stored<double> invoiced) => new("5f1e9a2b3c4d5e6f70860001", invoiced, 1, state, "x", false, 0, 0, 0, 0, 0, [],
        "5f1e9a2b3c4d5e6f70810001", "Ana", Now, Now, new JobHours(0, 0, 0, "a"),
        new JobMachiningStatistic(new JobHours(0, 0, 0, "b"), "n/a", "n/a", "n/a", null), null, "Média", 0);

    private static BudgetTask Task(double quantity) => new(Guid.NewGuid().ToString("N")[..24], "b", TaskTypes.Development, "", 30, quantity, 0);

    private static JsonElement Json(string text) => JsonElement.Parse(text);
}
