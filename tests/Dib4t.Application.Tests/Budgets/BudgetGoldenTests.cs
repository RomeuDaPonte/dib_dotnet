using System.Text;
using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Domain.Budgets;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Budgets;

/// <summary>
/// The legacy budget contract, case by case from the legacy code's own answers (<c>budget-golden.json</c>): the same
/// Joi 400 texts (create, clone, the intended edit schema of D7, search), the decided D21 refusal where legacy accepts
/// a <c>/</c>, the numbering, the counting period and the total of a new budget. (Documents and saves are checked
/// on MongoDB in <c>Dib4t.Infrastructure.Tests</c>.)
/// </summary>
public sealed class BudgetGoldenTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.BudgetGolden));

    public static TheoryData<int> NewBudgetCases => Indexes("newBudget");

    public static TheoryData<int> UpdateCases => Indexes("update");

    public static TheoryData<int> SearchCases => Indexes("search");

    [Theory]
    [MemberData(nameof(NewBudgetCases))]
    public void Create_and_clone_bodies_are_validated_like_the_legacy_code(int index)
    {
        var item = Golden.GetProperty("newBudget")[index];
        var body = item.GetProperty("body").GetString()!;

        var validated = BudgetValidators.ValidateNew(JsonElement.Parse(body));

        if (Message(item) is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        var properties = JsonElement.Parse(body);
        var descriptive = Last(body, "descriptive");
        if (descriptive.Contains('/', StringComparison.Ordinal))
        {
            validated.Error.Should().Be(Error.Validation(BudgetMessages.DescriptiveWithSlash), "D21: legacy accepts it, " + body);
            return;
        }

        validated.Value.Should().Be(new NewBudgetInput(
            properties.GetProperty("clientId").GetString()!.ToLowerInvariant(),
            properties.GetProperty("madeById").GetString()!.ToLowerInvariant(),
            descriptive.Normalize(NormalizationForm.FormC),
            properties.GetProperty("personInCharge").GetString()!.Normalize(NormalizationForm.FormC)), body);
    }

    [Theory]
    [MemberData(nameof(UpdateCases))]
    public void Edits_are_validated_with_the_schema_legacy_meant_to_apply(int index)
    {
        var item = Golden.GetProperty("update")[index];
        var body = item.GetProperty("body").GetString()!;
        item.GetProperty("legacyError").ValueKind.Should().Be(JsonValueKind.Null, "D7: legacy itself checks nothing");

        var validated = BudgetValidators.ValidateUpdate(JsonElement.Parse(body));

        if (Message(item) is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
        }
        else
        {
            validated.IsSuccess.Should().BeTrue(body);
        }
    }

    [Theory]
    [MemberData(nameof(SearchCases))]
    public void Search_queries_are_validated_like_the_legacy_code(int index)
    {
        var item = Golden.GetProperty("search")[index];
        var query = item.GetProperty("query").GetString()!;

        var validated = BudgetValidators.ValidateSearch(item.GetProperty("parsed"));

        if (Message(item) is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), query);
            return;
        }

        validated.Value.Should().Be(item.GetProperty("parsed").GetProperty("q").GetString(), "the text is the pattern: " + query);
        if (item.GetProperty("outcome").GetString() == "200")
        {
            // What the legacy driver sends: RegExp#source (with / and line breaks escaped, which PCRE reads the same) and "i".
            item.GetProperty("flags").GetString().Should().Be("i");
        }
    }

    [Fact]
    public void Numbers_are_the_legacy_ones()
    {
        foreach (var item in Golden.GetProperty("budgetNumbers").EnumerateArray())
        {
            BudgetNumber.Next(2026, item.GetProperty("count").GetInt32()).Should().Be(item.GetProperty("number").GetString());
        }

        var count = Golden.GetProperty("countForThisYear");
        count.GetProperty("distinctField").GetString().Should().Be("budgetNumber");
        DateTime.Parse(count.GetProperty("filter").GetProperty("date").GetProperty("$gte").GetProperty("$date").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().Be(BudgetNumber.YearStart(2026), "legacy new Date(\"2026\"): 1 January 00:00 UTC");
    }

    [Fact]
    public void A_new_budget_costs_one_automation_hour_with_the_default_margin()
    {
        var compared = 0;
        foreach (var item in Golden.GetProperty("create").EnumerateArray().Where(c => c.GetProperty("outcome").GetString() == "200"))
        {
            var prices = item.GetProperty("prices");
            var total = BudgetTotals.NewBudgetTotal(prices.GetProperty("margin").GetDouble(), prices.GetProperty("automation").GetDouble());
            total.Should().Be(item.GetProperty("response").GetProperty("total").GetDouble(), prices.GetRawText());
            compared++;
        }

        compared.Should().Be(5);
    }

    private static string? Message(JsonElement item) =>
        item.GetProperty("message").ValueKind == JsonValueKind.Null ? null : item.GetProperty("message").GetString();

    /// <summary>The last value of a key (a duplicate key keeps its last value, like JSON.parse).</summary>
    private static string Last(string body, string key) =>
        JsonElement.Parse(body).EnumerateObject().Last(p => p.Name == key).Value.GetString()!;

    private static TheoryData<int> Indexes(string section) => [.. Enumerable.Range(0, Golden.GetProperty(section).GetArrayLength())];
}
