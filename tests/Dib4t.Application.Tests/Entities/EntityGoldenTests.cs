using System.Text;
using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Entities;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Entities;

/// <summary>
/// The legacy entity contract, case by case from the legacy code's own answers (<c>entity-golden.json</c>): the same
/// Joi 400 text, the same refusal (500) where the Mongoose model refuses a body Joi accepted, the same values to
/// store, and the same search patterns. (The saves are checked on MongoDB in <c>Dib4t.Infrastructure.Tests</c>, the
/// patterns MongoDB refuses there too.)
/// </summary>
public sealed class EntityGoldenTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.EntityGolden));

    public static TheoryData<int> CreateCases => Indexes("createEntity");

    public static TheoryData<int> EditCases => Indexes("editEntity");

    public static TheoryData<int> SearchCases => Indexes("search");

    [Theory]
    [MemberData(nameof(CreateCases))]
    public void Create_validates_casts_and_stores_like_the_legacy_code(int index)
    {
        var item = Golden.GetProperty("createEntity")[index];
        var body = item.GetProperty("body").GetString()!;

        var validated = EntityValidators.Validate(JsonElement.Parse(body));

        if (Message(item) is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        var input = validated.Value.ToInput();
        if (item.GetProperty("outcome").GetString() == "500")
        {
            input.Error.Should().BeEquivalentTo(Error.Failure(LegacyMessages.SomethingFailed), o => o.Excluding(e => e.Reason), body);
            return;
        }

        var stored = item.GetProperty("stored");
        var value = input.Value;
        value.Name.Should().Be(Nfc(stored.GetProperty("name").GetString()!), "stored as NFC (§4.3 rule 2): " + body);
        value.PaymentCondition.Should().Be(stored.GetProperty("paymentCondition").GetString());
        value.Type.Should().Be(stored.GetProperty("type").GetString());
        Text(value.FiscalNumber).Should().Be(Stored(stored, "fiscalNumber"), body);
        Text(value.Address).Should().Be(Stored(stored, "address"), body);
        Text(value.ZipCode).Should().Be(Stored(stored, "zipCode"), body);
        Text(value.Location).Should().Be(Stored(stored, "location"), body);
    }

    [Theory]
    [MemberData(nameof(EditCases))]
    public void Edit_validates_and_refuses_like_the_legacy_code(int index)
    {
        var item = Golden.GetProperty("editEntity")[index];
        var body = item.GetProperty("body").GetString()!;

        var validated = EntityValidators.Validate(JsonElement.Parse(body));

        if (Message(item) is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        validated.Value.ToInput().IsSuccess.Should().Be(item.GetProperty("outcome").GetString() == "200", body);
    }

    [Theory]
    [MemberData(nameof(SearchCases))]
    public void Search_builds_the_legacy_patterns(int index)
    {
        var item = Golden.GetProperty("search")[index];
        var body = item.GetProperty("body").GetString()!;

        var search = EntitySearch.From(JsonElement.Parse(body));

        if (item.GetProperty("outcome").GetString() == "500" && !item.GetProperty("reason").GetString()!.StartsWith("SyntaxError", StringComparison.Ordinal))
        {
            search.Error.Should().BeEquivalentTo(Error.Failure(LegacyMessages.SomethingFailed), o => o.Excluding(e => e.Reason), body);
            return;
        }

        // A JavaScript SyntaxError: the pattern is built and MongoDB refuses it (Dib4t.Infrastructure.Tests).
        search.IsSuccess.Should().BeTrue(body);
        if (item.TryGetProperty("regexes", out var regexes))
        {
            search.Value.Patterns.Select(p => (p.Field, JsSource(p.Pattern), "i"))
                .Should().Equal(regexes.EnumerateArray().Select(r =>
                    (r.GetProperty("field").GetString()!, r.GetProperty("source").GetString()!, r.GetProperty("flags").GetString()!)), body);
        }
    }

    [Fact]
    public void Path_ids_cast_like_mongoose_findById()
    {
        foreach (var item in Golden.GetProperty("pathIds").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expected = item.GetProperty("cast").GetString() == "ok" ? item.GetProperty("objectId").GetString() : null;

            LegacyObjectId.Cast(id).Should().Be(expected, id);
        }
    }

    [Fact]
    public void Types_are_the_legacy_getAllEntityTypes() =>
        EntitiesService.Types.Should().Equal(Golden.GetProperty("entityTypes").EnumerateArray()
            .Select(t => (t.GetProperty("_id").GetInt32(), t.GetProperty("name").GetString()!)));

    [Fact]
    public void JavaScript_string_conversion_of_json_values()
    {
        string? Convert(string json) => EntitySearch.JsString(JsonElement.Parse(json));

        Convert("[[1,[2]],null,true]").Should().Be("1,2,,true");
        Convert("""{"valueOf":1}""").Should().Be("[object Object]", "only an own toString key breaks the conversion");
        Convert("""{"toString":"x"}""").Should().BeNull();
        Convert("-0").Should().Be("0");
        Convert("1e-7").Should().Be("1e-7");
    }

    /// <summary>The <c>source</c> V8 gives the pattern (what the legacy driver sends): <c>/</c> and line terminators escaped.</summary>
    private static string JsSource(string pattern) => pattern
        .Replace("/", "\\/", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
        .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static (bool Present, string? Value) Text(EntityText text) => (text.IsPresent, text.Value);

    private static (bool Present, string? Value) Stored(JsonElement stored, string field) =>
        stored.TryGetProperty(field, out var value) ? (true, value.ValueKind == JsonValueKind.Null ? null : Nfc(value.GetString()!)) : (false, null);

    private static string? Message(JsonElement item) =>
        item.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;

    private static string Nfc(string value) => value.Normalize(NormalizationForm.FormC);

    private static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Golden.GetProperty(section).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }
}
