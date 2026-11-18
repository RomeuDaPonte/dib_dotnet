using System.Text.Json;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Calculations;
using Dib4t.Tests.Common;

namespace Dib4t.Domain.Tests.SharedKernel;

/// <summary>
/// The job details page's cost summary and table against <c>job-costs-golden.json</c>: the legacy front-end's
/// <c>taskHelper.js</c> and <c>useTaskHoursRowState.js</c> run verbatim in Node on 135 scenarios (stored numbers and
/// typed texts, nulls, no tasks, hours without tasks of the type, fractional prices, seeded random ones). Every
/// printed text and color class is the legacy one, except the three fixed defects, checked explicitly here.
/// </summary>
public sealed class JobCostCalculationsTests
{
    private const string UndefinedMarker = "__undefined__";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobCostsGolden));

    public static TheoryData<int> Scenarios
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < Golden.GetProperty("scenarios").GetArrayLength(); i++)
            {
                data.Add(i);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_scenario_prints_what_the_legacy_page_prints(int index)
    {
        var scenario = Golden.GetProperty("scenarios")[index];
        var name = scenario.GetProperty("name").GetString();
        var expected = scenario.TryGetProperty("fixed", out var fixedPage) ? fixedPage : scenario.GetProperty("page");

        var page = Calculate(scenario);

        var header = expected.GetProperty("header");
        var summary = page.Summary;
        summary.TasksTotal.Should().Be(header.GetProperty("tasksTotal").GetString(), name);
        summary.BudgetTotal.Should().Be(header.GetProperty("budgetTotal").GetString(), name);
        summary.Registered.Should().Be(header.GetProperty("registered").GetString(), name);
        ShouldMatch(summary.PreviewedProfit, header.GetProperty("previewedProfit"), name);
        ShouldMatch(summary.Deviation, header.GetProperty("deviation"), name);
        ShouldMatch(summary.TotalProfit, header.GetProperty("totalProfit"), name);
        ShouldMatch(summary.DeviationPercent, header.GetProperty("deviationPercent"), name);
        ShouldMatch(summary.TotalPercent, header.GetProperty("totalPercent"), name);

        // fixed: the margin is colored by its sign (legacy: always "profit")
        var margin = header.GetProperty("margin");
        header.GetProperty("margin").GetProperty("class").GetString().Should().Be(JobCostCalculations.Profit);
        summary.Margin.Text.Should().Be(margin.GetProperty("text").GetString(), name);
        summary.Margin.CssClass.Should().Be(JobCostCalculations.Class(JsNumber.FromText(margin.GetProperty("text").GetString()![..^2])), name);

        var rows = expected.GetProperty("rows").EnumerateArray().ToList();
        page.Rows.Should().HaveCount(rows.Count);
        foreach (var (row, legacy) in page.Rows.Zip(rows))
        {
            var because = $"{name}: {legacy.GetProperty("label").GetString()}";
            row.Label.Should().Be(legacy.GetProperty("label").GetString(), because);
            row.Budgeted.Should().Be(legacy.GetProperty("budgeted").GetString(), because);
            row.Deviation.Should().Be(legacy.GetProperty("deviation").GetString(), because);
            var field = legacy.GetProperty("field");
            if (field.ValueKind == JsonValueKind.Null)
            {
                row.Field.Should().BeNull(because);
            }
            else
            {
                row.Field.Should().NotBeNull(because);
                row.Field!.Value.ToString().Should().Be(field.ValueKind == JsonValueKind.Number ? JsNumber.ToString(field.GetDouble()) : field.GetString(), because);
            }

            // fixed: an infinite percentage (nothing budgeted) is "-" (legacy prints "Infinity %" / "-Infinity %")
            var percent = legacy.GetProperty("percent");
            if (percent.GetProperty("text").GetString() is "Infinity %" or "-Infinity %")
            {
                row.Percent.Should().Be(new ColoredText("-", null), because);
            }
            else
            {
                ShouldMatch(row.Percent, percent, because);
            }
        }
    }

    [Fact]
    public void A_job_without_statistics_counts_zero_hours_where_the_legacy_page_crashes()
    {
        var crashed = Golden.GetProperty("scenarios").EnumerateArray().Where(s => s.GetProperty("page").TryGetProperty("crash", out _)).ToList();
        crashed.Should().ContainSingle();
        crashed[0].GetProperty("page").GetProperty("crash").GetString().Should().StartWith("TypeError: Cannot read properties of undefined");
        crashed[0].TryGetProperty("fixed", out _).Should().BeTrue();
    }

    [Fact]
    public void The_scenarios_cover_the_fixed_percentages_and_negative_margins()
    {
        var scenarios = Golden.GetProperty("scenarios").EnumerateArray().Select(s => s.GetProperty("page")).Where(p => p.TryGetProperty("rows", out _)).ToList();
        scenarios.SelectMany(p => p.GetProperty("rows").EnumerateArray()).Count(r => r.GetProperty("percent").GetProperty("text").GetString()!.Contains("Infinity", StringComparison.Ordinal))
            .Should().BeGreaterThan(10);
        scenarios.Count(p => p.GetProperty("header").GetProperty("margin").GetProperty("text").GetString()!.StartsWith('-')).Should().BeGreaterThan(5);
    }

    [Theory]
    [InlineData(4, 20.5, 35, "12", 420)] // average 35.00 × 12
    [InlineData(4, 2.5, 33.33, "3.7", 123)] // Math.floor(33.33 × 3.7 = 123.32…)
    [InlineData(4, 0, 0, "3", 105)] // no budgeted hours: the hourly price (35) × 3
    [InlineData(4, 3, 10, "", 0)]
    public void The_price_of_used_hours_is_floored_at_the_average_price(int _, double quantity, double unitPrice, string hours, double expected)
    {
        IReadOnlyList<CostTask> tasks = quantity == 0 ? [] : [new CostTask("development", quantity, unitPrice)];
        var prices = new Dictionary<string, double>(StringComparer.Ordinal) { ["development"] = 35 };
        JobCostCalculations.PriceForTask(tasks, "development", JsValue.Text(hours), prices).Should().Be(expected);
    }

    private static void ShouldMatch(ColoredText actual, JsonElement expected, string? because)
    {
        actual.Text.Should().Be(expected.GetProperty("text").GetString(), because);
        actual.CssClass.Should().Be(expected.GetProperty("class").GetString(), because);
    }

    private static JobCostPage Calculate(JsonElement scenario)
    {
        var tasks = scenario.GetProperty("tasks").EnumerateArray()
            .Select(t => new CostTask(t.GetProperty("taskType").GetString(), Number(t.GetProperty("quantity")), Number(t.GetProperty("unitPrice"))))
            .ToList();
        var job = scenario.GetProperty("job");
        var values = new JobCostValues(
            Hours(job, "devStatistic"), Hours(job, "machiningStatistic"), Hours(job, "assemblyStatistic"),
            Value(job.GetProperty("components")), Value(job.GetProperty("automation")), Value(job.GetProperty("materials")),
            Value(job.GetProperty("others")), Value(job.GetProperty("costNonQuality")));
        var prices = scenario.GetProperty("prices").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
        return JobCostCalculations.Page(tasks, values, Number(scenario.GetProperty("budgetTotal")), prices);
    }

    private static JsValue Hours(JsonElement job, string statistic) =>
        job.GetProperty(statistic) is { ValueKind: JsonValueKind.Object } s ? Value(s.GetProperty("numberOfHoursUsed")) : JsValue.Undefined;

    private static JsValue Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => JsValue.Of(value.GetDouble()),
        JsonValueKind.Null => JsValue.Null,
        JsonValueKind.String when value.GetString() == UndefinedMarker => JsValue.Undefined,
        JsonValueKind.String => JsValue.Text(value.GetString()),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.GetRawText(), null),
    };

    private static double Number(JsonElement value) => value.GetDouble();
}
