using System.Text.Json;
using Dib4t.Application.Jobs;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// The job details page's rules from the legacy code's own answers (<c>job-details-golden.json</c>): the Joi 400 texts
/// of <c>statisticsDates</c> and <c>singleProperty</c>, and the job model's assignments (<see cref="JobChanges"/>): the
/// date, registered value, single property and verification casts, D17 and D18. (Answers and documents are checked on
/// MongoDB over HTTP in <c>Dib4t.Server.Tests/Jobs/JobDetailsGoldenTests</c>.)
/// </summary>
public sealed class JobDetailsRulesTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobDetailsGolden));

    private static readonly DateTime Now = new(2026, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc);

    /// <summary>The golden cases of the two validated routes answered with a Joi text on an existing job id.</summary>
    public static TheoryData<int> JoiCases
    {
        get
        {
            var data = new TheoryData<int>();
            var cases = Golden.GetProperty("cases");
            for (var i = 0; i < cases.GetArrayLength(); i++)
            {
                var outcome = cases[i].GetProperty("legacy");
                if (outcome.GetProperty("status").GetInt32() == 400 && outcome.GetProperty("text").GetString()!.StartsWith('"')
                    && cases[i].GetProperty("section").GetString() is "statisticsDates" or "singleProperty")
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
        var last = item.GetProperty("path").GetString()!.Split('/')[^1];
        var body = JsonElement.Parse(item.GetProperty("body").GetString()!);

        var message = !last.StartsWith("{{", StringComparison.Ordinal)
            ? JobValidators.ValidateId("jobId", last).Error?.Message
            : item.GetProperty("section").GetString() == "statisticsDates"
                ? JobValidators.ValidateStateDateName(body).Error?.Message
                : JobValidators.ValidateSingleProperty(body).Error?.Message;

        message.Should().Be(item.GetProperty("legacy").GetProperty("text").GetString(), item.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("\"2026-03-10\"", "jobStatisticsDates.devEndedInDate", "2026-03-10T00:00:00.0000000Z")]
    [InlineData("\"\"", "jobStatisticsDates.devEndedInDate", null)]
    [InlineData("1773100800000", "jobStatisticsDates.devEndedInDate", "2026-03-10T00:00:00.0000000Z")]
    [InlineData("\"2026-02-30\"", "jobStatisticsDates.devEndedInDate", "2026-03-02T00:00:00.0000000Z")]
    public void A_state_date_of_existing_dates_is_a_dotted_path(string json, string path, string? expected)
    {
        var changes = Changes(Job() with { StatisticsDates = Dates() });
        changes.SetStatisticsDate("devEndedInDate", JsonElement.Parse(json));

        changes.Refusal.Should().BeNull();
        changes.Assignments.Should().ContainSingle().Which.Should().Be(new JobAssignment(path,
            expected is null ? StoredValue.Null : StoredValue.Date(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind))));
    }

    [Fact]
    public void D17_a_job_without_state_dates_gets_a_new_sub_document_with_the_date()
    {
        var changes = Changes(Job());
        changes.SetStatisticsDate("approvedEndedInDate", JsonElement.Parse("\"2026-03-10\""));

        var assignment = changes.Assignments.Should().ContainSingle().Which;
        assignment.Path.Should().Be("jobStatisticsDates");
        ((StoredValue.DocumentValue)assignment.Value).Fields.Should().Equal(
            new KeyValuePair<string, StoredValue>("_id", StoredValue.NewId),
            new KeyValuePair<string, StoredValue>("approvedEndedInDate", StoredValue.Date(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"10000-01-01\"")]
    public void An_invalid_or_too_late_date_is_refused(string json)
    {
        var changes = Changes(Job() with { StatisticsDates = Dates() });
        changes.SetStatisticsDate("devEndedInDate", JsonElement.Parse(json));
        changes.Refusal.Should().NotBeNull();
    }

    [Theory]
    [InlineData("development", "devStatistic.numberOfHoursUsed")]
    [InlineData("consultancy", "devStatistic.numberOfHoursUsed")] // D18
    [InlineData("assembly", "assemblyStatistic.numberOfHoursUsed")]
    [InlineData("machining", "machiningStatistic.numberOfHoursUsed")]
    [InlineData("automation", "automation")]
    [InlineData("components", "components")]
    [InlineData("others", "others")]
    [InlineData("custoNaoQualidade", "costNonQuality")]
    [InlineData("materials", "materials")]
    public void A_registered_value_goes_to_the_types_field_as_mongoose_casts_it(string type, string path)
    {
        var changes = Changes(Job());
        changes.SetRegisteredValue(JsonElement.Parse("\" 12.5 \""), JsonElement.Parse($"\"{type}\""));

        changes.Assignments.Should().Equal(new JobAssignment(path, StoredValue.Number(12.5)));
    }

    [Theory]
    [InlineData("\"Development\"")]
    [InlineData("\"outro\"")]
    [InlineData("1")]
    [InlineData(null)]
    public void Another_type_changes_nothing(string? type)
    {
        var changes = Changes(Job());
        changes.SetRegisteredValue(JsonElement.Parse("\"12\""), type is null ? null : JsonElement.Parse(type));
        changes.Assignments.Should().BeEmpty();
    }

    [Fact]
    public void Registering_hours_without_the_statistic_is_the_legacy_type_error()
    {
        var changes = Changes(Job() with { DevStatistic = null });
        var act = () => changes.SetRegisteredValue(JsonElement.Parse("5"), JsonElement.Parse("\"development\""));
        act.Should().Throw<JobScriptException>().WithMessage("TypeError: Cannot set properties of undefined (setting 'numberOfHoursUsed')");
    }

    [Theory]
    [InlineData("orderIdentification", "\"\"", true)]
    [InlineData("orderIdentification", "null", true)]
    [InlineData("orderIdentification", null, true)]
    [InlineData("orderIdentification", "5", false)]
    [InlineData("isOfficialOrderIdentification", "\"TRUE\"", true)]
    [InlineData("isOfficialOrderIdentification", "1", false)]
    [InlineData("invoicedPercent", "\"abc\"", true)]
    [InlineData("invoicedPercent", "150", false)]
    [InlineData("comments", "[\"a\"]", true)]
    [InlineData("deliveryDate", "\"abc\"", true)]
    public void A_single_property_is_refused_where_the_legacy_save_fails(string property, string? json, bool refused)
    {
        var changes = Changes(Job());
        changes.SetSingleProperty(property, json is null ? null : JsonElement.Parse(json));
        (changes.Refusal is not null).Should().Be(refused);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("\"1\"", true)]
    [InlineData("[1]", true)]
    [InlineData("\"true\"", false)]
    [InlineData("false", false)]
    [InlineData("{}", false)]
    [InlineData(null, false)]
    public void The_machining_flag_is_javascript_loose_equality_with_true(string? json, bool machining) =>
        JobChanges.IsMachiningVerification(json is null ? null : JsonElement.Parse(json)).Should().Be(machining);

    [Fact]
    public void A_replaced_statistic_keeps_the_sent_id_and_takes_the_users_name()
    {
        var changes = Changes(Job());
        changes.ReplaceMachiningStatistic(JsonElement.Parse("""
            { "numberOfHoursUsed": "12", "verificationAndShapeControl": "Ok", "_id": "5F1E9A2B3C4D5E6F70880004", "validatedBy": "Outro", "extra": 1 }
            """), "Ana Administradora");

        var fields = ((StoredValue.DocumentValue)changes.Assignments.Single().Value).Fields;
        fields.Select(f => f.Key).Should().Equal("numberOfHoursUsed", "budgetedHours", "deviation", "verificationAndShapeControl",
            "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish", "_id", "validatedBy");
        fields[0].Value.Should().Be(StoredValue.Number(12));
        fields[4].Value.Should().Be(StoredValue.Text(QuestionResponses.NotApplicable));
        fields[6].Value.Should().Be(StoredValue.ObjectId("5f1e9a2b3c4d5e6f70880004"));
        fields[7].Value.Should().Be(StoredValue.Text("Ana Administradora"));
    }

    [Fact]
    public void A_wrong_answer_or_id_is_refused()
    {
        var answer = Changes(Job());
        answer.ReplaceAssemblyStatistic(JsonElement.Parse("""{ "validTryOnFirstTest": "4" }"""), "Ana");
        answer.Refusal.Should().NotBeNull();

        var id = Changes(Job());
        id.ReplaceAssemblyStatistic(JsonElement.Parse("""{ "_id": "abc" }"""), "Ana");
        id.Refusal.Should().NotBeNull();
    }

    private static JobChanges Changes(Job job) => new(job, Now);

    private static JobStatisticsDates Dates() => new("s", Stored<DateTime>.Of(Now), Stored<DateTime>.Missing, Stored<DateTime>.Missing,
        Stored<DateTime>.Missing, Stored<DateTime>.Missing, Stored<DateTime>.Missing);

    private static readonly Stored<double> Zero = Stored<double>.Of(0);

    private static Job Job() => new("5f1e9a2b3c4d5e6f70860004", Zero, 1, JobStates.Machining, "x", Stored<bool>.Of(false), Zero, Zero, Zero, Zero, Zero, [],
        "5f1e9a2b3c4d5e6f70810004", "Ana", Now, Now, new JobHours(Zero, 0, 0, "a"),
        new JobMachiningStatistic(new JobHours(Zero, 0, 0, "b"), "n/a", "n/a", "n/a", null),
        new JobAssemblyStatistic(new JobHours(Zero, 0, 0, "c"), "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "1", null), "Média", 0);
}
