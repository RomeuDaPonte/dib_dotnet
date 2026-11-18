using System.Globalization;
using System.Text;
using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Jobs;
using Dib4t.Application.Printing;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel.Vocabulary;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// The legacy job create contract, case by case from the legacy code's own answers (<c>job-golden.json</c>): the same
/// Joi 400 texts, the values Mongoose stores (the original values' casts: dates, numbers, the flag), the cases the
/// legacy save refuses after Joi (D26), the budgeted hours and the label payloads. (Documents are checked on MongoDB
/// over HTTP in <c>Dib4t.Server.Tests</c>.)
/// </summary>
public sealed class JobGoldenTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobGolden));

    /// <summary>Texts only V8's fallback date parser reads (a known difference, see <see cref="JsDate"/>).</summary>
    private static readonly HashSet<string> V8OnlyDates = new(StringComparer.Ordinal) { "2030/01/15", "Jan 15 2030", " 2030-01-15 " };

    public static TheoryData<int> CreateCases => [.. Enumerable.Range(0, Golden.GetProperty("create").GetArrayLength())];

    public static TheoryData<int> LabelCases => [.. Enumerable.Range(0, Golden.GetProperty("labels").GetArrayLength())];

    [Theory]
    [MemberData(nameof(CreateCases))]
    public void Bodies_are_validated_and_cast_like_the_legacy_code(int index)
    {
        var item = Golden.GetProperty("create")[index];
        var body = item.GetProperty("body").GetString()!.Replace("{{budget}}", Golden.GetProperty("ids").GetProperty("{{budget}}").GetString(), StringComparison.Ordinal);

        var validated = JobValidators.ValidateNew(JsonElement.Parse(body));

        if (item.TryGetProperty("text", out var text) && item.GetProperty("status").GetInt32() == 400)
        {
            validated.Error.Should().Be(Error.Validation(text.GetString()!), body);
            return;
        }

        var due = JsonElement.Parse(body).GetProperty("dueDate");
        if (due.ValueKind == JsonValueKind.String && V8OnlyDates.Contains(due.GetString()!))
        {
            validated.Error.Should().Be(Error.Validation("\"dueDate\" must be a number of milliseconds or valid date string"), "V8's fallback parser: " + body);
            return;
        }

        validated.IsSuccess.Should().BeTrue(body);
        var input = validated.Value;
        if (item.TryGetProperty("error", out var error))
        {
            error.GetString().Should().Contain("Cast to Boolean failed");
            input.Refusal.Should().StartWith("isOfficialOrderIdentification: Cast to Boolean failed", "D26: " + body);
            return;
        }

        var stored = item.GetProperty("effects").EnumerateArray().Single(e => e.TryGetProperty("insert", out _)).GetProperty("document");
        var storedDue = stored.GetProperty("dueDate").GetProperty("$date").GetString()!;
        if (storedDue.StartsWith('+'))
        {
            input.Refusal.Should().Be($"dueDate: {storedDue} is outside the years 1–9999", "beyond .NET's DateTime: " + body);
            return;
        }

        input.Refusal.Should().BeNull(body);
        input.DueDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture).Should().Be(storedDue, body);
        input.FinalPrice.Should().Be(Number(stored.GetProperty("finalPrice")), body);
        input.IsOfficialOrderIdentification.Should().Be(stored.GetProperty("isOfficialOrderIdentification").GetBoolean(), body);
        input.State.Should().Be(stored.GetProperty("state").GetString(), body);
        input.OrderIdentification.Should().Be(stored.GetProperty("orderIdentification").GetString()!.Normalize(NormalizationForm.FormC), body);
        input.BudgetId.Should().Be(Golden.GetProperty("ids").GetProperty("{{budget}}").GetString());
    }

    [Theory]
    [MemberData(nameof(LabelCases))]
    public void Label_payloads_are_the_legacy_ones_in_the_companys_zone(int index)
    {
        var item = Golden.GetProperty("labels")[index];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.GetProperty("timeZone").GetString()!);
        var start = DateTime.Parse(item.GetProperty("startDate").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var sent = item.GetProperty("sent").EnumerateArray().Single();

        var payload = LabelTemplates.Payload(item.GetProperty("templateName").GetString(), item.GetProperty("budgetNumber").GetString(),
            item.GetProperty("descriptive").GetString(), item.GetProperty("client").GetString(), DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(start, zone)));

        payload.Should().Be(sent.GetProperty("send").GetString());
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(payload + "\n")).Should().Be(sent.GetProperty("wireHex").GetString(), "UTF8(template + \"\\n\")");
        (sent.GetProperty("connectTimeoutMs").GetInt32(), sent.GetProperty("negotiationMandatory").GetBoolean()).Should().Be((1500, false));
    }

    [Fact]
    public void A_label_of_another_template_is_empty_and_missing_values_read_undefined()
    {
        LabelTemplates.Payload("outro", "26-0001", "x", "y", new DateOnly(2026, 1, 2)).Should().BeEmpty();
        LabelTemplates.Payload(PrintTemplates.Maquinacao, null, null, null, new DateOnly(2026, 1, 2))
            .Should().Be("\u001bia3,^TS002undefined,2/1/2026,undefined,undefined,");
    }

    [Fact]
    public void Budgeted_hours_are_the_legacy_sums_per_type()
    {
        var tasks = Golden.GetProperty("tasks").EnumerateArray().Select(t => new BudgetTask(
            t.GetProperty("_id").GetProperty("$oid").GetString()!, t.GetProperty("budgetId").GetProperty("$oid").GetString(), t.GetProperty("taskType").GetString(),
            null, null, t.TryGetProperty("quantity", out var q) ? Number(q) : null, 0)).ToList();
        foreach (var item in Golden.GetProperty("shapes").EnumerateArray().Where(s => s.GetProperty("status").GetInt32() == 200))
        {
            var budgetId = JsonElement.Parse(item.GetProperty("body").GetString()!).GetProperty("budgetId").GetString(); // a name, as in "tasks"
            var own = tasks.Where(t => t.BudgetId == budgetId).ToList();
            var job = item.GetProperty("effects").EnumerateArray().Single(e => e.TryGetProperty("insert", out _)).GetProperty("document");
            foreach (var (field, type) in new[] { ("devStatistic", TaskTypes.Development), ("machiningStatistic", TaskTypes.Machining), ("assemblyStatistic", TaskTypes.Assembly) })
            {
                JobStatistics.BudgetedHours(own, type).Should().Be(Number(job.GetProperty(field).GetProperty("budgetedHours")), $"{budgetId} {field}");
            }
        }

        JobStatistics.BudgetedHours([new BudgetTask("1", "b", TaskTypes.Development, null, null, null, 0)], TaskTypes.Development).Should().Be(double.NaN);
    }

    private static double Number(JsonElement value) =>
        double.Parse((value.TryGetProperty("$numberInt", out var i) ? i : value.GetProperty("$numberDouble")).GetString()!, CultureInfo.InvariantCulture);
}

/// <summary>
/// <see cref="JsDate"/> against ECMAScript (the values Node 16 gives for <c>new Date(…)</c>, <c>toISOString</c>).
/// </summary>
public sealed class JsDateTests
{
    [Theory]
    [InlineData("2030-01-15", "2030-01-15T00:00:00.000Z")]
    [InlineData("2030-01", "2030-01-01T00:00:00.000Z")]
    [InlineData("2030", "2030-01-01T00:00:00.000Z")]
    [InlineData("+002030-01-15", "2030-01-15T00:00:00.000Z")]
    [InlineData("-000001-12-31T00:00Z", "-000001-12-31T00:00:00.000Z")]
    [InlineData("2030-01-15T10:00", "2030-01-15T10:00:00.000Z")]
    [InlineData("2030-01-15T10:00:00.5Z", "2030-01-15T10:00:00.500Z")]
    [InlineData("2030-01-15T10:00:00.12345Z", "2030-01-15T10:00:00.123Z")]
    [InlineData("2030-01-15T10:00:00+01:00", "2030-01-15T09:00:00.000Z")]
    [InlineData("2030-01-15T00:30:00-02:30", "2030-01-15T03:00:00.000Z")]
    [InlineData("2030-02-30", "2030-03-02T00:00:00.000Z")]
    [InlineData("2028-02-29", "2028-02-29T00:00:00.000Z")]
    [InlineData("2030-01-15T24:00", "2030-01-16T00:00:00.000Z")]
    [InlineData("1969-12-31T23:59:59.999Z", "1969-12-31T23:59:59.999Z")]
    [InlineData("0022-07-29", "0022-07-29T00:00:00.000Z")]
    public void Iso_texts_parse_like_ecmascript(string text, string iso) =>
        JsDate.ToIsoString(JsDate.Parse(text)!.Value).Should().Be(iso);

    [Theory]
    [InlineData("2030-13-01")]
    [InlineData("2030-01-32")]
    [InlineData("2030-00-10")]
    [InlineData("2030-01-15T24:01")]
    [InlineData("2030-01-15T10:60")]
    [InlineData("2030-01-15T10:00:00+24:00")]
    [InlineData("-000000-01-01")]
    [InlineData("+275760-09-13T00:00:00.001Z")]
    [InlineData("2030-1-15")]
    [InlineData("")]
    [InlineData("abc")]
    public void Other_texts_are_invalid_dates(string text) => JsDate.Parse(text).Should().BeNull();

    [Fact]
    public void Time_values_are_clipped_like_ecmascript()
    {
        JsDate.TimeClip(1.9).Should().Be(1);
        JsDate.TimeClip(-1.9).Should().Be(-1);
        JsDate.TimeClip(8.64e15).Should().Be(8_640_000_000_000_000);
        JsDate.TimeClip(8.64e15 + 1).Should().BeNull();
        JsDate.TimeClip(double.NaN).Should().BeNull();
        JsDate.ToIsoString(8_640_000_000_000_000).Should().Be("+275760-09-13T00:00:00.000Z");
        JsDate.ToIsoString(-62_198_755_200_000).Should().Be("-000001-01-01T00:00:00.000Z");
        JsDate.ToDateTime(8_640_000_000_000_000).Should().BeNull();
        JsDate.ToDateTime(0).Should().Be(DateTime.UnixEpoch);
    }

    [Theory]
    [InlineData("\"2030\"", 2030L)] // Joi: parseFloat, 2,030 ms
    [InlineData("\"1.5\"", 1L)]
    [InlineData("\"-3\"", -3L)]
    [InlineData("1894665600000", 1894665600000L)]
    [InlineData("true", null)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("1e400", null)]
    public void Joi_reads_digits_as_milliseconds(string json, long? expected) => JsDate.JoiToDate(JsonElement.Parse(json)).Should().Be(expected);

    [Theory]
    [InlineData("\"2030\"", "2030-01-01T00:00:00.000Z")] // Mongoose: below 275761, a date text
    [InlineData("\"275761\"", "1970-01-01T00:04:35.761Z")]
    [InlineData("\"1894665600000\"", "2030-01-15T00:00:00.000Z")]
    [InlineData("1.5", "1970-01-01T00:00:00.001Z")]
    [InlineData("\"-271821\"", "1969-12-31T23:55:28.179Z")]
    public void Mongoose_casts_the_original_value(string json, string iso) =>
        JsDate.ToIsoString(MongooseCasts.Date(JsonElement.Parse(json))!.Value).Should().Be(iso);

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"yes\"", true)]
    [InlineData("1", true)]
    [InlineData("\"0\"", false)]
    [InlineData("\"no\"", false)]
    [InlineData("\"TRUE\"", null)]
    [InlineData("\"False\"", null)]
    [InlineData("null", null)]
    public void Mongoose_casts_booleans_exactly(string json, bool? expected) => MongooseCasts.Boolean(JsonElement.Parse(json)).Should().Be(expected);
}
