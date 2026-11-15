using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dib4t.Server.Http;
using Dib4t.Server.Logging;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;

namespace Dib4t.Server.Tests.Logging;

/// <summary>
/// The logging foundation end to end (PLAN.md §3.3, §11.9): one <c>Http</c> entry per API request, its Mongo commands
/// and use-case events under the same request id, the legacy 500 with <c>x-request-id</c> (D16), no secrets.
/// </summary>
public sealed class LoggingPipelineTests(LoggingApp app) : IClassFixture<LoggingApp>
{
    private const string PasswordHash = "$2a$10$MarkerHashThatMustNeverBeLoggedXXXXXXXXXXXXXXXXXXXXX";

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin); // the restore endpoint needs the settings role

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_api_request_is_one_Http_entry_and_its_Mongo_commands_and_use_case_events_share_its_request_id()
    {
        var user = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "email", "ana@dib4t.local" }, { "password", PasswordHash } };

        using var response = await RestoreAsync(DibbakTestWriter.Valid(("users", [user])).ToFile());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var requestId = RequestIdOf(response);
        var rows = await app.RowsAsync(rows => rows.Any(row => row.RequestId == requestId && row.Category == "Http"));
        var request = rows.Where(row => row.RequestId == requestId).ToList();

        var http = request.Should().ContainSingle(row => row.Category == "Http").Subject;
        http.Level.Should().Be("Information");
        http.Message.Should().MatchRegex(@"^HTTP POST /api/database/restore responded 200 in \d+\.\d ms$");
        var properties = JsonElement.Parse(http.PropertiesJson!);
        properties.GetProperty("RouteTemplate").GetString().Should().Be("/api/database/restore");
        properties.GetProperty("StatusCode").GetInt32().Should().Be(200);
        properties.GetProperty("Elapsed").GetDouble().Should().BePositive();

        var database = request.Where(row => row.Category == "Database").ToList();
        database.Select(row => row.Message).Should().Contain(message => message.StartsWith("Database restore started", StringComparison.Ordinal))
            .And.Contain("Database restored: 1 collections, 1 documents");
        var commands = database.Where(row => row.SourceContext == "Dib4t.Infrastructure.Mongo.MongoCommandLogger").ToList();
        // The token check reads the signed-in user first ("find" on users), unless it is still cached (PLAN.md §4.6);
        // "drop": the test client's user is in "users" (TestUserHandler). Only the _id_ index: no createIndexes.
        commands.Select(row => JsonElement.Parse(row.PropertiesJson!).GetProperty("CommandName").GetString())
            .SkipWhile(name => name == "find")
            .Should().Equal("listCollections", "drop", "create", "insert");
        commands.Should().AllSatisfy(row =>
        {
            var command = JsonElement.Parse(row.PropertiesJson!);
            command.GetProperty("DurationMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
            command.GetProperty("Database").GetString().Should().Be(app.Database.DatabaseNamespace.DatabaseName);
        });
        commands.Should().ContainSingle(row => row.Message.Contains(" insert ", StringComparison.Ordinal))
            .Which.Message.Should().Contain(".users in ").And.EndWith("n=1");
        rows.Should().AllSatisfy(row => row.ToString().Should().NotContain(PasswordHash));
    }

    [Fact]
    public async Task An_unhandled_exception_is_the_legacy_500_with_the_request_id_and_the_stack_trace_only_in_the_store()
    {
        using var response = await _client.GetAsync(new Uri("/api/test-logging/throw", UriKind.Relative), Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body.Should().Be(LegacyExceptionHandler.Message);
        response.Content.Headers.ContentType!.ToString().Should().Be(LegacyResults.TextContentType);
        var requestId = RequestIdOf(response);

        var rows = await app.RowsAsync(rows => rows.Any(row => row.RequestId == requestId && row.Category == "Http"));
        var request = rows.Where(row => row.RequestId == requestId).ToList();
        var error = request.Should().ContainSingle(row => row.Exception != null).Subject;
        error.Level.Should().Be("Error");
        error.Category.Should().Be("App");
        error.SourceContext.Should().Be(typeof(LegacyExceptionHandler).FullName);
        error.Message.Should().Be("Unhandled exception on GET /api/test-logging/throw");
        error.Exception.Should().Contain($"InvalidOperationException: {LoggingTestController.ExceptionText}")
            .And.Contain(nameof(LoggingTestController.Throw));
        request.Should().ContainSingle(row => row.Category == "Http").Which.Should()
            .Match<LogRow>(row => row.Level == "Error" && row.Message.Contains("responded 500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_4xx_answer_is_a_Warning()
    {
        using var response = await RestoreAsync([]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var requestId = RequestIdOf(response);
        var rows = await app.RowsAsync(rows => rows.Any(row => row.RequestId == requestId && row.Category == "Http"));
        rows.Should().ContainSingle(row => row.RequestId == requestId && row.Category == "Http").Which.Level.Should().Be("Warning");
    }

    [Fact]
    public async Task A_secret_in_a_log_call_is_masked_in_the_store()
    {
        using var response = await _client.PostAsync(new Uri("/api/test-logging/login", UriKind.Relative), null, Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var requestId = RequestIdOf(response);
        var rows = await app.RowsAsync(rows => rows.Any(row => row.RequestId == requestId && row.Category == "Http"));
        rows.Should().ContainSingle(row => row.RequestId == requestId && row.Message.StartsWith("Login attempt", StringComparison.Ordinal))
            .Which.Message.Should().Be("Login attempt ana@dib4t.local with ***");
        rows.Should().AllSatisfy(row => row.ToString().Should().NotContain(LoggingTestController.Password));
    }

    [Fact]
    public async Task Pages_and_static_files_are_not_logged_but_carry_a_request_id()
    {
        using var response = await _client.GetAsync(new Uri("/", UriKind.Relative), Cancellation);
        var requestId = RequestIdOf(response);
        using var marker = await RestoreAsync([]); // a later request: once it is in the store, the page request would be too
        var markerId = RequestIdOf(marker);

        var rows = await app.RowsAsync(rows => rows.Any(row => row.RequestId == markerId && row.Category == "Http"));

        rows.Should().NotContain(row => row.RequestId == requestId);
    }

    [Fact]
    public async Task Startup_is_logged_with_version_and_environment()
    {
        var rows = await app.RowsAsync(rows => rows.Any(row => row.Message.StartsWith("Dib4t ", StringComparison.Ordinal)));

        rows.Should().Contain(row => row.Message == "Dib4t 1.0.0 starting (Development)" && row.Category == "App");
    }

    private static string RequestIdOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(LoggingSetup.RequestIdHeader, out var values).Should().BeTrue("every response carries x-request-id");
        return values!.Should().ContainSingle().Subject.Should().MatchRegex("^[0-9a-f]{32}$").And.Subject;
    }

    private async Task<HttpResponseMessage> RestoreAsync(byte[] backup)
    {
        using var content = new ByteArrayContent(backup);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
    }
}
