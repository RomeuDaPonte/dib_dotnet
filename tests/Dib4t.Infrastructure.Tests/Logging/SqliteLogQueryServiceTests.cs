using Dib4t.Application.Logs;
using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common.Logging;
using static Dib4t.Infrastructure.Tests.Logging.LogStoreSeed;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>Registos queries on the SQLite file (PLAN.md §3.3): filters, FTS5, newest first, keyset paging, users.</summary>
public sealed class SqliteLogQueryServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2027, 3, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = LogStoreFile.NewTempPath();
    private readonly SqliteLogQueryService _service;

    public SqliteLogQueryServiceTests()
    {
        _service = new SqliteLogQueryService(new LogStoreLocation(_path));
        Write(_path,
            Entry(T0, "HTTP POST /api/account/create responded 200 in 12.0 ms", category: "Http", requestId: "req-a", user: "Ana"),
            Entry(T0.AddMilliseconds(1), "insert users: 1 document", category: "Database", requestId: "req-a", user: "Ana",
                properties: """{"Command":"insert","Collection":"users"}"""),
            Entry(T0.AddMilliseconds(1), "User eva.teste@dib4t.local created", category: "Database", requestId: "req-a", user: "Ana"),
            Entry(T0.AddMinutes(1), "HTTP GET /api/prices responded 500 in 3.1 ms", level: "Error", category: "Http", requestId: "req-b", user: "Bruno",
                exception: "System.TimeoutException: socket stalled"),
            Entry(T0.AddMinutes(2), "Database restore started", level: "Warning", category: "Database"),
            Entry(T0.AddHours(-30), "Old entry outside the default range"));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public void Dispose() => LogStoreFile.Delete(_path);

    private static LogSearch Search(
        string[]? levels = null, string[]? categories = null, string? user = null, string? requestId = null, string? text = null,
        LogCursor? after = null, int limit = 100, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(from ?? T0.AddHours(-24), to ?? T0.AddHours(1), levels ?? [], categories ?? [], user, requestId,
            LogFullTextQuery.Translate(text), after, limit);

    private async Task<string[]> MessagesAsync(LogSearch search) =>
        [.. (await _service.SearchAsync(search, Cancellation)).Items.Select(i => i.Message)];

    [Fact]
    public async Task Newest_first_within_the_range_with_every_column()
    {
        var page = await _service.SearchAsync(Search(), Cancellation);

        page.Items.Select(i => i.Message).Should().Equal(
            "Database restore started",
            "HTTP GET /api/prices responded 500 in 3.1 ms",
            "User eva.teste@dib4t.local created", // same millisecond: higher id first
            "insert users: 1 document",
            "HTTP POST /api/account/create responded 200 in 12.0 ms");
        page.Next.Should().BeNull();
        var error = page.Items[1];
        error.Should().BeEquivalentTo(new LogRecord(error.Id, T0.AddMinutes(1), "Error", "Http", null,
            "HTTP GET /api/prices responded 500 in 3.1 ms", "System.TimeoutException: socket stalled", null, "req-b", "Bruno"));
        (await MessagesAsync(Search(from: T0.AddHours(-31), to: T0.AddHours(-29)))).Should().Equal("Old entry outside the default range");
    }

    [Fact]
    public async Task Levels_categories_user_and_request_id_filter()
    {
        (await MessagesAsync(Search(levels: ["Error", "Warning"]))).Should().HaveCount(2);
        (await MessagesAsync(Search(categories: ["Database"]))).Should().HaveCount(3);
        (await MessagesAsync(Search(user: "Bruno"))).Should().Equal("HTTP GET /api/prices responded 500 in 3.1 ms");
        (await MessagesAsync(Search(requestId: "req-a"))).Should().Equal(
            "User eva.teste@dib4t.local created", "insert users: 1 document", "HTTP POST /api/account/create responded 200 in 12.0 ms");
        (await MessagesAsync(Search(requestId: "req-a", categories: ["Http"], levels: ["Information"]))).Should().ContainSingle();
    }

    [Fact]
    public async Task Full_text_search_covers_message_exception_and_properties_with_prefixes_and_phrases()
    {
        (await MessagesAsync(Search(text: "restor"))).Should().Equal("Database restore started");
        (await MessagesAsync(Search(text: "\"socket stalled\""))).Should().Equal("HTTP GET /api/prices responded 500 in 3.1 ms");
        (await MessagesAsync(Search(text: "\"stalled socket\""))).Should().BeEmpty();
        (await MessagesAsync(Search(text: "collection users"))).Should().Equal("insert users: 1 document"); // properties
        (await MessagesAsync(Search(text: "eva.teste@dib4t"))).Should().Equal("User eva.teste@dib4t.local created");
        (await MessagesAsync(Search(text: "responded", categories: ["Http"], user: "Ana"))).Should().ContainSingle();
        (await MessagesAsync(Search(text: "NOT OR \"AND\""))).Should().BeEmpty("operators are plain words, never FTS5 syntax");
    }

    [Fact]
    public async Task Keyset_pages_never_skip_or_repeat_entries_with_equal_timestamps()
    {
        var all = await MessagesAsync(Search());
        var seen = new List<string>();
        LogCursor? after = null;
        var pages = 0;
        do
        {
            var page = await _service.SearchAsync(Search(limit: 2, after: after), Cancellation);
            seen.AddRange(page.Items.Select(i => i.Message));
            after = page.Next;
            pages++;
        }
        while (after is not null);

        seen.Should().Equal(all);
        pages.Should().Be(3);
    }

    [Fact]
    public async Task A_full_last_page_has_no_cursor()
    {
        var page = await _service.SearchAsync(Search(limit: 5), Cancellation);

        page.Items.Should().HaveCount(5);
        page.Next.Should().BeNull("there is nothing older in the range");
    }

    [Fact]
    public async Task Users_are_distinct_and_sorted() =>
        (await _service.UsersAsync(Cancellation)).Should().Equal("Ana", "Bruno");
}
