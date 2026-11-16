using System.Diagnostics;
using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common.Logging;
using Serilog;
using Serilog.Core;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>The SQLite log store file: schema, settings, column mapping, full-text search (PLAN.md §3.3).</summary>
public sealed class SqliteLogStoreTests : IAsyncDisposable
{
    private readonly string _path = LogStoreFile.NewTempPath();
    private readonly LogStoreSink _sink;
    private readonly Logger _logger;

    public SqliteLogStoreTests()
    {
        _sink = new LogStoreSink(new SqliteLogStore(_path), new LogsOptions(), TimeProvider.System);
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _logger.DisposeAsync();
        await _sink.DisposeAsync();
        LogStoreFile.Delete(_path);
    }

    [Fact]
    public async Task New_file_has_the_schema_and_settings()
    {
        await _sink.FlushAsync(Cancellation);

        (await LogStoreFile.ScalarAsync(_path, "PRAGMA journal_mode;", Cancellation)).Should().Be("wal");
        (await LogStoreFile.ScalarAsync(_path, "PRAGMA auto_vacuum;", Cancellation)).Should().Be(2L); // INCREMENTAL
        (await LogStoreFile.ScalarAsync(_path, "SELECT schema_version FROM meta WHERE id = 1;", Cancellation)).Should().Be(2L);
        (await LogStoreFile.ScalarAsync(_path, "SELECT last_cleanup_utc FROM meta WHERE id = 1;", Cancellation)).Should().Be(DBNull.Value);
        var objects = (string)(await LogStoreFile.ScalarAsync(_path,
            "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name NOT LIKE 'logs_fts_%' ORDER BY name);",
            Cancellation))!;
        objects.Split(',').Should().BeEquivalentTo(
            "logs", "logs_fts", "logs_ai", "logs_ad", "meta",
            "ix_logs_timestamp", "ix_logs_level_timestamp", "ix_logs_category_timestamp", "ix_logs_request_id", "ix_logs_user_timestamp");
    }

    [Fact]
    public async Task Reopening_an_existing_file_keeps_its_entries()
    {
        _logger.Information("before");
        await _sink.FlushAsync(Cancellation);
        await _sink.DisposeAsync();

        await using (var reopened = new LogStoreSink(new SqliteLogStore(_path), new LogsOptions(), TimeProvider.System))
        {
            reopened.Emit(LogEvents.Create(1));
            await reopened.FlushAsync(Cancellation);
        }

        (await LogStoreFile.ReadAsync(_path, Cancellation)).Select(row => row.Message).Should().Equal("before", "Event 1");
    }

    [Fact]
    public async Task Columns_hold_level_category_source_message_exception_properties_request_and_user()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        _logger.ForContext(Constants.SourceContextPropertyName, "Dib4t.Application.Database.DatabaseRestoreService")
            .ForContext("RequestId", "req-1")
            .ForContext("UserName", "Ana")
            .Error(new InvalidOperationException("boom"), "Restore of {Bytes} bytes from {File} as {@Options} ({Padded,6})", 42, "a.dibbak", new { Mode = "full" }, "x");
        await _sink.FlushAsync(Cancellation);

        var row = (await LogStoreFile.ReadAsync(_path, Cancellation)).Should().ContainSingle().Subject;
        row.Level.Should().Be("Error");
        row.Category.Should().Be("Database");
        row.SourceContext.Should().Be("Dib4t.Application.Database.DatabaseRestoreService");
        // strings without quotes (Registos); objects and aligned values as Serilog renders them
        row.Message.Should().Be("Restore of 42 bytes from a.dibbak as { Mode: \"full\" } (   \"x\")");
        row.Exception.Should().StartWith("System.InvalidOperationException: boom");
        row.PropertiesJson.Should().Be("""{"Bytes":42,"File":"a.dibbak","Options":{"Mode":"full"},"Padded":"x"}""");
        row.RequestId.Should().Be("req-1");
        row.UserName.Should().Be("Ana");
        row.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Explicit_category_wins_and_unknown_sources_are_App()
    {
        _logger.ForContext(Constants.SourceContextPropertyName, "Dib4t.Application.Database.X").ForContext("Category", "Jobs").Information("explicit");
        _logger.ForContext(Constants.SourceContextPropertyName, "Some.Library").Information("library");
        _logger.Information("no source");
        await _sink.FlushAsync(Cancellation);

        (await LogStoreFile.ReadAsync(_path, Cancellation)).Select(row => (row.Message, row.Category, row.PropertiesJson))
            .Should().Equal(("explicit", "Jobs", null), ("library", "App", null), ("no source", "App", null));
    }

    [Fact]
    public async Task The_trace_id_is_the_request_id_and_the_RequestId_property_the_fallback()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        _logger.Information("in a trace");
        _logger.ForContext("RequestId", "scope-id").Information("in a trace with a request scope");
        activity.Stop();
        _logger.ForContext("RequestId", "scope-id").Information("request scope only");
        _logger.Information("outside");
        await _sink.FlushAsync(Cancellation);

        var traceId = activity.TraceId.ToHexString();
        (await LogStoreFile.ReadAsync(_path, Cancellation)).Select(row => row.RequestId)
            .Should().Equal(traceId, traceId, "scope-id", null);
    }

    [Fact]
    public async Task Full_text_search_covers_message_exception_and_properties_and_follows_deletes()
    {
        _logger.Information("Database restore started");
        _logger.Error(new TimeoutException("socket stalled"), "Request failed");
        _logger.Information("Printing {Template}", "etiqueta");
        await _sink.FlushAsync(Cancellation);

        async Task<long> CountAsync(string query) =>
            (long)(await LogStoreFile.ScalarAsync(_path, $"SELECT count(*) FROM logs_fts WHERE logs_fts MATCH '{query}';", Cancellation))!;

        (await CountAsync("restor*")).Should().Be(1);
        (await CountAsync("\"socket stalled\"")).Should().Be(1);
        (await CountAsync("etiqueta")).Should().Be(1);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await connection.OpenAsync(Cancellation);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM logs WHERE message LIKE 'Database%';";
            await delete.ExecuteNonQueryAsync(Cancellation);
        }

        (await CountAsync("restor*")).Should().Be(0, "the delete trigger keeps the index in step (log cleanup)");
    }

    [Theory]
    [InlineData("Serilog.AspNetCore.RequestLoggingMiddleware", "Http")]
    [InlineData("Dib4t.Infrastructure.Mongo.MongoCommandLogger", "Database")]
    [InlineData("Dib4t.Infrastructure.Backup.MongoDatabaseBackupService", "Database")]
    [InlineData("Dib4t.Application.Database.DatabaseRestoreService", "Database")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler", "Auth")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService", "Auth")]
    [InlineData("Dib4t.Infrastructure.Logging.LogCleanupService", "Jobs")]
    [InlineData("Dib4t.Application.DatabaseX", "App")]
    [InlineData("Dib4t.Server.Http.LegacyExceptionHandler", "App")]
    [InlineData(null, "App")]
    public void Source_context_maps_to_a_category(string? sourceContext, string category) =>
        LogCategoryMap.Resolve(sourceContext).Should().Be(category);
}
