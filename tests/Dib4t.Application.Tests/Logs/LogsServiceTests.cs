using Dib4t.Application.Common;
using Dib4t.Application.Logs;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Logs;

/// <summary>The Registos filters (PLAN.md §3.3): defaults, validation texts, lists, cursor, limit and the FTS5 translation.</summary>
public sealed class LogsServiceTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 31, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static LogSearchInput Input(
        string? from = null, string? to = null, string[]? levels = null, string[]? categories = null, string? user = null,
        string? requestId = null, string? text = null, string? cursor = null, string? limit = null) =>
        new(from, to, levels ?? [], categories ?? [], user, requestId, text, cursor, limit);

    private static LogSearch Valid(LogSearchInput input)
    {
        var result = LogsService.Parse(input, Now);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        return result.Value;
    }

    private static string Invalid(LogSearchInput input)
    {
        var result = LogsService.Parse(input, Now);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        return result.Error.Message;
    }

    [Fact]
    public void Without_filters_the_last_24_hours_newest_first_100_per_page()
    {
        var search = Valid(Input());

        search.Should().BeEquivalentTo(new LogSearch(Now.AddHours(-24), Now, [], [], null, null, null, null, 100));
    }

    [Theory]
    [InlineData("2027-03-30T08:15:00Z", "2027-03-30T08:15:00+00:00")]
    [InlineData("2027-03-30T08:15:00.123+01:00", "2027-03-30T07:15:00.123+00:00")]
    [InlineData("2027-03-30T08:15", "2027-03-30T08:15:00+00:00")] // no offset: UTC
    [InlineData("2027-03-30", "2027-03-30T00:00:00+00:00")]
    public void Dates_are_ISO_8601(string text, string expected) =>
        Valid(Input(from: text)).From.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void A_from_without_to_ends_now_and_a_to_without_from_starts_24_h_before_it()
    {
        Valid(Input(from: "2027-03-01")).To.Should().Be(Now);
        Valid(Input(to: "2027-03-02T00:00:00Z")).From.Should().Be(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("ontem")]
    [InlineData("31/03/2027")]
    [InlineData("2027-02-30")]
    [InlineData("1711843200000")]
    public void Bad_dates_are_refused(string text)
    {
        Invalid(Input(from: text)).Should().Be("Filtro inválido: data inicial (from).");
        Invalid(Input(to: text)).Should().Be("Filtro inválido: data final (to).");
    }

    [Fact]
    public void From_after_to_is_refused() =>
        Invalid(Input(from: "2027-03-02", to: "2027-03-01")).Should().Be("Filtro inválido: a data inicial é depois da data final.");

    [Fact]
    public void Levels_and_categories_are_comma_separated_or_repeated_any_case_without_duplicates()
    {
        var search = Valid(Input(levels: ["error, WARNING", "Error"], categories: ["http,database", "Nas"]));

        search.Levels.Should().Equal("Error", "Warning");
        search.Categories.Should().Equal("Http", "Database", "Nas");
    }

    [Fact]
    public void Unknown_levels_and_categories_are_refused()
    {
        Invalid(Input(levels: ["Error,Critical"])).Should().Be("Filtro inválido: nível desconhecido \"Critical\".");
        Invalid(Input(categories: ["Budgets"])).Should().Be("Filtro inválido: categoria desconhecida \"Budgets\".");
    }

    [Fact]
    public void User_and_request_id_are_trimmed_and_empty_means_no_filter()
    {
        var search = Valid(Input(user: "  Ana Administradora ", requestId: " 4bf92f3577b34da6a3ce929d0e0e4736 ", text: "   "));

        (search.User, search.RequestId, search.FullTextQuery).Should().Be(("Ana Administradora", "4bf92f3577b34da6a3ce929d0e0e4736", null));
        Valid(Input(user: "", requestId: " ")).Should().Match<LogSearch>(s => s.User == null && s.RequestId == null);
    }

    [Fact]
    public void Overlong_texts_are_refused()
    {
        Invalid(Input(user: new string('a', 201))).Should().Be("Filtro inválido: user tem mais de 200 caracteres.");
        Invalid(Input(requestId: new string('a', 201))).Should().Be("Filtro inválido: requestId tem mais de 200 caracteres.");
        Invalid(Input(text: new string('a', 501))).Should().Be("Filtro inválido: q tem mais de 500 caracteres.");
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("200", 200)]
    [InlineData(" 50 ", 50)]
    public void Limit_is_1_to_200(string text, int limit) => Valid(Input(limit: text)).Limit.Should().Be(limit);

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-5")]
    [InlineData("1e2")]
    [InlineData("dez")]
    public void Other_limits_are_refused(string text) =>
        Invalid(Input(limit: text)).Should().Be("Filtro inválido: limit tem de ser um número de 1 a 200.");

    [Fact]
    public void The_cursor_is_timestamp_dash_id()
    {
        Valid(Input(cursor: "1806480000000-42")).After.Should().Be(new LogCursor(1806480000000, 42));
        new LogCursor(1806480000000, 42).ToString().Should().Be("1806480000000-42");
        foreach (var bad in new[] { "42", "-42", "a-1", "1-b", "1-2-3", "1.5-2" })
        {
            Invalid(Input(cursor: bad)).Should().Be("Filtro inválido: cursor.", bad);
        }
    }

    [Theory]
    [InlineData("restor", "\"restor\"*")]
    [InlineData("restor*", "\"restor\"*")]
    [InlineData("  restore   failed ", "\"restore\"* \"failed\"*")]
    [InlineData("\"socket stalled\"", "\"socket stalled\"")]
    [InlineData("POST \"responded 500\" printer", "\"POST\"* \"responded 500\" \"printer\"*")]
    [InlineData("\"unclosed phrase", "\"unclosed phrase\"")]
    [InlineData("ana.admin@dib4t.local", "\"ana.admin@dib4t.local\"*")]
    [InlineData("NOT OR AND NEAR(a b)", "\"NOT\"* \"OR\"* \"AND\"* \"NEAR(a\"* \"b)\"*")]
    [InlineData("message:boom", "\"message:boom\"*")]
    [InlineData("Orçamento nº 21-0042", "\"Orçamento\"* \"nº\"* \"21-0042\"*")]
    [InlineData("- * \"\" \" \"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Search_text_becomes_quoted_FTS5_terms(string? text, string? expected) =>
        LogFullTextQuery.Translate(text).Should().Be(expected);

    [Fact]
    public async Task Search_validates_then_reads_the_store_and_facets_list_the_vocabularies_and_users()
    {
        var store = Substitute.For<ILogQueryService>();
        var page = new LogPage([], null);
        store.SearchAsync(Arg.Any<LogSearch>(), Arg.Any<CancellationToken>()).Returns(page);
        store.UsersAsync(Arg.Any<CancellationToken>()).Returns(["Ana", "Bruno"]);
        var service = new LogsService(store, new FakeTimeProvider(Now));

        (await service.SearchAsync(Input(text: "boom"), Cancellation)).Value.Should().BeSameAs(page);
        await store.Received(1).SearchAsync(Arg.Is<LogSearch>(s => s.To == Now && s.FullTextQuery == "\"boom\"*"), Arg.Any<CancellationToken>());
        (await service.SearchAsync(Input(limit: "0"), Cancellation)).IsSuccess.Should().BeFalse();
        await store.Received(1).SearchAsync(Arg.Any<LogSearch>(), Arg.Any<CancellationToken>());

        var facets = await service.FacetsAsync(Cancellation);
        facets.Categories.Should().Equal("Database", "Http", "Nas", "Printer", "App", "Jobs", "Auth");
        facets.Levels.Should().Equal("Fatal", "Error", "Warning", "Information", "Debug", "Verbose");
        facets.Users.Should().Equal("Ana", "Bruno");
    }
}
