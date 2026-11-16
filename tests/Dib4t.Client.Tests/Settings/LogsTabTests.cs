using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Components.Settings;
using Dib4t.Client.Settings;
using Dib4t.Contracts.Logs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Settings;

/// <summary>The Registos tab (PLAN.md §3.3) against a substituted <see cref="ILogsApi"/> and a fake clock.</summary>
public sealed class LogsTabTests : IAsyncDisposable
{
    private const string RequestA = "4bf92f3577b34da6a3ce929d0e0e4736";
    private static readonly DateTimeOffset Now = new(2027, 7, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private readonly ClientTestContext _context = new();
    private readonly ILogsApi _logs = Substitute.For<ILogsApi>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<string> _queries = [];

    public LogsTabTests()
    {
        _context.Services.AddSingleton(_logs);
        _context.Services.AddSingleton<TimeProvider>(_time);
        _logs.FacetsAsync().Returns(ApiResult.Success(new LogFacetsResponse(
            ["Database", "Http", "Nas", "Printer", "App", "Jobs", "Auth"], ["Fatal", "Error", "Warning", "Information", "Debug", "Verbose"], ["Ana", "Bruno"])));
        _logs.SearchAsync(Arg.Do<string>(_queries.Add)).Returns(call => Page(call.Arg<string>()));
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    private static ApiResult<LogPageResponse> Page(string query) => query.Contains("cursor=", StringComparison.Ordinal)
        ? ApiResult.Success(new LogPageResponse([Entry(1, "older entry", Now.AddHours(-5))], null))
        : ApiResult.Success(new LogPageResponse(
            [
                Entry(3, "HTTP GET /api/prices responded 500 in 3.1 ms", Now.AddMinutes(-1), "Error", "Http", exception: "System.TimeoutException: socket stalled"),
                Entry(2, "Mongo insert dib4t.users in 8.5 ms n=1", Now.AddMinutes(-2), properties: """{"CommandName":"insert","Result":"n=1"}"""),
            ], "1800000000000-2"));

    private static LogEntryResponse Entry(long id, string message, DateTimeOffset at, string level = "Information", string category = "Database",
        string? exception = null, string? properties = null) =>
        new(id, at, level, category, "Dib4t.Test", message, exception, properties, RequestA, "Ana");

    private IRenderedComponent<LogsTab> Render()
    {
        _context.Render<MudPopoverProvider>();
        return _context.Render<LogsTab>(p => p.Add(t => t.Zone, Lisbon));
    }

    [Fact]
    public void The_last_24_hours_are_listed_newest_first_with_local_times_and_level_chips()
    {
        var tab = Render();

        _queries.Should().Equal("from=2027-07-14T09%3A30%3A00.0000000%2B00%3A00&limit=100");
        var rows = tab.FindAll("tr.logs-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".logs-time")!.TextContent.Trim().Should().Be("15/07/2027 10:29:00.000"); // WEST
        rows[0].QuerySelector(".logs-level--error")!.TextContent.Should().Be("Erro");
        rows[0].TextContent.Should().Contain("Http").And.Contain("Ana").And.Contain("HTTP GET /api/prices responded 500");
        tab.Find("#logs-user").QuerySelectorAll("option").Select(o => o.TextContent).Should().Equal("Todos", "Ana", "Bruno");
    }

    [Fact]
    public async Task A_row_expands_to_its_request_properties_and_exception()
    {
        var tab = Render();

        await tab.FindAll(".logs-expand")[1].ClickAsync(new MouseEventArgs());

        tab.FindAll(".logs-expand")[1].GetAttribute("aria-expanded").Should().Be("true");
        var detail = tab.Find("#logs-detail-2");
        detail.QuerySelector(".logs-request")!.TextContent.Should().Be(RequestA);
        detail.QuerySelector(".logs-pre")!.TextContent.Should().Be("{\n  \"CommandName\": \"insert\",\n  \"Result\": \"n=1\"\n}");
        await tab.FindAll(".logs-expand")[0].ClickAsync(new MouseEventArgs());
        tab.Find("#logs-detail-3").TextContent.Should().Contain("System.TimeoutException: socket stalled");
    }

    [Fact]
    public async Task Clicking_a_request_id_shows_every_entry_of_that_request()
    {
        var tab = Render();
        await tab.Find("#logs-search").InputAsync(new ChangeEventArgs { Value = "boom" });
        await tab.FindAll(".logs-expand")[0].ClickAsync(new MouseEventArgs());

        await tab.Find(".logs-request").ClickAsync(new MouseEventArgs());

        _queries[^1].Should().Be($"from=2027-07-14T09%3A30%3A00.0000000%2B00%3A00&requestId={RequestA}&limit=100");
        tab.Find("#logs-request-id").GetAttribute("value").Should().Be(RequestA);
    }

    [Fact]
    public async Task Filters_are_sent_on_search_and_more_entries_load_with_the_cursor()
    {
        var tab = Render();
        await tab.Find("#logs-range").ChangeAsync(new ChangeEventArgs { Value = "LastHour" });
        await tab.FindAll("fieldset.logs-checks")[0].QuerySelectorAll("input")[1].ChangeAsync(new ChangeEventArgs { Value = true }); // Erro
        await tab.FindAll("fieldset.logs-checks")[1].QuerySelectorAll("input")[1].ChangeAsync(new ChangeEventArgs { Value = true }); // Http
        await tab.Find("#logs-user").ChangeAsync(new ChangeEventArgs { Value = "Bruno" });
        await tab.Find("#logs-search").InputAsync(new ChangeEventArgs { Value = "\"responded 500\"" });

        await tab.Find("form").SubmitAsync();

        _queries[^1].Should().Be("from=2027-07-15T08%3A30%3A00.0000000%2B00%3A00&levels=Error&categories=Http&user=Bruno&q=%22responded%20500%22&limit=100");
        await tab.Find(".logs-more").ClickAsync(new MouseEventArgs());
        _queries[^1].Should().EndWith("&cursor=1800000000000-2&limit=100");
        tab.FindAll("tr.logs-row").Should().HaveCount(3);
        tab.FindAll(".logs-more").Should().BeEmpty("the last page has no cursor");
    }

    [Fact]
    public void A_refused_filter_shows_the_server_text()
    {
        _logs.SearchAsync(Arg.Any<string>()).Returns(ApiResult.Failure<LogPageResponse>(System.Net.HttpStatusCode.BadRequest, "Filtro inválido: cursor."));
        var tab = Render();

        tab.Find(".logs-error").TextContent.Should().Be("Filtro inválido: cursor.");
    }

    [Fact]
    public void Time_ranges_today_and_custom_are_local_and_custom_has_an_end()
    {
        var view = new LogsView(_logs, _time) { Zone = Lisbon };

        view.SetRange(LogRange.Today);
        view.TimeRange().Should().Be((new DateTimeOffset(2027, 7, 14, 23, 0, 0, TimeSpan.Zero), (DateTimeOffset?)null));
        view.SetRange(LogRange.Last7Days);
        view.TimeRange().From.Should().Be(Now.AddDays(-7));
        view.SetRange(LogRange.Custom);
        (view.CustomFrom, view.CustomTo).Should().Be(("2027-07-14T10:30", "2027-07-15T10:30"));
        view.CustomFrom = "2027-01-10T08:00";
        view.TimeRange().Should().Be((new DateTimeOffset(2027, 1, 10, 8, 0, 0, TimeSpan.Zero), (DateTimeOffset?)new DateTimeOffset(2027, 7, 15, 9, 30, 0, TimeSpan.Zero)));
        view.BuildQuery(null).Should().StartWith("from=2027-01-10T08%3A00%3A00.0000000%2B00%3A00&to=2027-07-15T09%3A30%3A00.0000000%2B00%3A00&");
    }

    [Fact]
    public async Task Auto_refresh_reloads_every_10_seconds_and_only_for_ranges_that_end_now()
    {
        var tab = Render();
        _queries.Should().HaveCount(1);

        await tab.Find(".logs-actions input").ChangeAsync(new ChangeEventArgs { Value = true });
        _time.Advance(TimeSpan.FromSeconds(10));
        tab.WaitForAssertion(() => _queries.Should().HaveCount(2));
        _time.Advance(TimeSpan.FromSeconds(10));
        tab.WaitForAssertion(() => _queries.Should().HaveCount(3));

        await tab.Find("#logs-range").ChangeAsync(new ChangeEventArgs { Value = "Custom" });
        _time.Advance(TimeSpan.FromSeconds(30));
        _queries.Should().HaveCount(3, "a custom range does not end now");
        tab.Find(".logs-actions input").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task On_phones_the_filters_fold_behind_a_toggle()
    {
        var tab = Render();
        tab.Find("#logs-filters").ClassList.Should().NotContain("logs-filters--open");

        var toggle = tab.Find(".logs-filters-toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        await toggle.ClickAsync(new MouseEventArgs());

        tab.Find(".logs-filters-toggle").GetAttribute("aria-expanded").Should().Be("true");
        tab.Find("#logs-filters").ClassList.Should().Contain("logs-filters--open");
    }
}
