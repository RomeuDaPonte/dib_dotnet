using System.Text;
using System.Text.Json;
using Dib4t.Server.Logging;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;

namespace Dib4t.Server.Tests.Logging;

/// <summary>
/// <c>GET /api/logs</c> and <c>/facets</c> (Registos, PLAN.md §3.3): S only, plain-text 400s, and the main use: a
/// change's request found by its <c>x-request-id</c> (HTTP line, Mongo commands, use-case event) and by text.
/// </summary>
public sealed class LogsEndpointTests(LoggingApp app) : IClassFixture<LoggingApp>
{
    private readonly HttpClient _admin = app.CreateClient(TestUser.Admin);
    private readonly HttpClient _limited = app.CreateClient(TestUser.Limited);
    private readonly HttpClient _anonymous = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/logs")]
    [InlineData("/api/logs/facets")]
    public async Task Only_canSeeDefinicoes(string path)
    {
        var limited = await LegacyHttp.SendAsync(_limited, HttpMethod.Get, path, null, null, Cancellation);
        var anonymous = await LegacyHttp.SendAsync(_anonymous, HttpMethod.Get, path, null, null, Cancellation);

        (limited.Status, limited.Text).Should().Be((401, "Não tem permissão para concluir esta acção."));
        (anonymous.Status, anonymous.Text).Should().Be((401, "Access denied. No token provided."));
    }

    [Theory]
    [InlineData("from=ontem", "Filtro inválido: data inicial (from).")]
    [InlineData("from=2027-03-02&to=2027-03-01", "Filtro inválido: a data inicial é depois da data final.")]
    [InlineData("levels=Error,Critical", "Filtro inválido: nível desconhecido \"Critical\".")]
    [InlineData("categories=Budgets", "Filtro inválido: categoria desconhecida \"Budgets\".")]
    [InlineData("cursor=abc", "Filtro inválido: cursor.")]
    [InlineData("limit=201", "Filtro inválido: limit tem de ser um número de 1 a 200.")]
    public async Task Bad_filters_are_plain_text_400s(string query, string text)
    {
        var answer = await LegacyHttp.SendAsync(_admin, HttpMethod.Get, "/api/logs?" + query, null, null, Cancellation);

        (answer.Status, answer.ContentType, answer.Text).Should().Be((400, "text/html; charset=utf-8", text));
    }

    [Fact]
    public async Task A_change_is_found_by_its_request_id_and_by_text()
    {
        using var create = await _admin.PostAsync(new Uri("/api/printer-settings", UriKind.Relative),
            new StringContent("""{"ip":"10.1.2.77","port":9100,"templateName":"producao"}""", Encoding.UTF8, "application/json"), Cancellation);
        create.EnsureSuccessStatusCode();
        var requestId = create.Headers.GetValues(LoggingSetup.RequestIdHeader).Single();
        await app.RowsAsync(rows => rows.Any(row => row.RequestId == requestId && row.Category == "Http"));

        var page = await GetAsync($"/api/logs?requestId={requestId}");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("category").GetString()).Should().Contain(["Http", "Database", "Printer"]);
        items.Should().OnlyContain(i => i.GetProperty("requestId").GetString() == requestId);
        items.Should().OnlyContain(i => i.GetProperty("userName").GetString() == TestUser.Admin.Name);
        var http = items.Single(i => i.GetProperty("category").GetString() == "Http");
        http.GetProperty("message").GetString().Should().StartWith("HTTP POST /api/printer-settings responded 200 in ");
        http.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "timestamp", "level", "category", "sourceContext", "message", "exception", "properties", "requestId", "userName");
        items.Should().Contain(i => i.GetProperty("message").GetString()!.StartsWith("Mongo insert ", StringComparison.Ordinal));
        items.Select(i => i.GetProperty("timestamp").GetDateTimeOffset()).Should().BeInDescendingOrder();
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var found = await GetAsync("/api/logs?q=" + Uri.EscapeDataString("\"10.1.2.77\"") + "&categories=Printer");
        found.GetProperty("items").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("requestId").GetString().Should().Be(requestId);
    }

    [Fact]
    public async Task Pages_follow_the_cursor_and_browsing_the_logs_is_not_logged()
    {
        for (var i = 0; i < 3; i++)
        {
            (await _admin.GetAsync(new Uri("/api/prices", UriKind.Relative), Cancellation)).EnsureSuccessStatusCode();
        }

        await app.RowsAsync(rows => rows.Count(row => row.Message.StartsWith("HTTP GET /api/prices ", StringComparison.Ordinal)) >= 3);
        var ids = new List<long>();
        string? cursor = null;
        do
        {
            var page = await GetAsync("/api/logs?categories=Http&limit=1&q=prices" + (cursor is null ? string.Empty : "&cursor=" + cursor));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        ids.Should().HaveCountGreaterThanOrEqualTo(3).And.OnlyHaveUniqueItems().And.BeInDescendingOrder();
        var rows = await app.RowsAsync(_ => true);
        rows.Should().NotContain(row => row.Message.StartsWith("HTTP GET /api/logs responded 200", StringComparison.Ordinal),
            "a successful GET /api/logs is logged at Debug (refused ones stay Warnings)");
    }

    [Fact]
    public async Task Facets_list_the_categories_levels_and_users()
    {
        (await _admin.GetAsync(new Uri("/api/prices", UriKind.Relative), Cancellation)).EnsureSuccessStatusCode();
        await app.RowsAsync(rows => rows.Any(row => row.UserName == TestUser.Admin.Name));

        var facets = await GetAsync("/api/logs/facets");

        facets.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).Should().Equal("Database", "Http", "Nas", "Printer", "App", "Jobs", "Auth");
        facets.GetProperty("levels").EnumerateArray().Select(c => c.GetString()).Should().StartWith(["Fatal", "Error", "Warning", "Information"]);
        facets.GetProperty("users").EnumerateArray().Select(c => c.GetString()).Should().Contain(TestUser.Admin.Name);
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _admin.GetAsync(new Uri(path, UriKind.Relative), Cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
    }
}
