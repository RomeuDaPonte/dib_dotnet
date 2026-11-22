using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests;

/// <summary>
/// <c>GET /api/database/info</c> and <c>GET /api/database/backup</c> (PLAN.md §6, §7.6) against the recorded legacy
/// steps (<c>characterization/06-database.json</c>) on a real mongo:9.0.8, and the busy lock shared with the restore.
/// </summary>
public sealed partial class DatabaseBackupEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>, IAsyncLifetime
{
    private static readonly Uri InfoUri = new("/api/database/info", UriKind.Relative);
    private static readonly Uri BackupUri = new("/api/database/backup", UriKind.Relative);
    private static readonly Uri RestoreUri = new("/api/database/restore", UriKind.Relative);

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Every test starts on the anonymized production backup (8 collections, 30,754 documents), loaded through the restore endpoint.</summary>
    public async ValueTask InitializeAsync()
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _client.PostAsync(RestoreUri, content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await TestUser.Admin.SaveAsync(app.Database, Cancellation); // the restore replaced the users; the requests save it again anyway
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Info_matches_the_recorded_legacy_step_with_this_database()
    {
        var expected = (await RecordedStepAsync("db-info")).GetProperty("response");
        var before = DateTime.UtcNow.AddSeconds(-1);

        using var response = await _client.GetAsync(InfoUri, Cancellation);
        var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        Keys(json).Should().Equal(Keys(expected.GetProperty("json")), "the legacy keys in the legacy order");
        var row = expected.GetProperty("json").GetProperty("collectionsInfo")[0];
        foreach (var collection in json.GetProperty("collectionsInfo").EnumerateArray())
        {
            Keys(collection).Should().Equal(Keys(row));
        }

        var server = app.Factory.Services.GetRequiredService<IMongoDatabase>().Client.Settings.Server;
        json.GetProperty("name").GetString().Should().Be(app.Database.DatabaseNamespace.DatabaseName);
        json.GetProperty("host").GetString().Should().Be(server.Host);
        json.GetProperty("port").GetInt32().Should().Be(server.Port);
        json.GetProperty("serverVersion").GetString().Should().Be("9.0.8");
        json.GetProperty("busyWith").ValueKind.Should().Be(JsonValueKind.Null);
        var fetchedAt = json.GetProperty("fetchedAt").GetString()!;
        fetchedAt.Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        DateTime.Parse(fetchedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);

        // The fixture plus the signed-in user (saved before each request), in the legacy order.
        var counts = await CountsAsync();
        json.GetProperty("collections").GetInt32().Should().Be(8);
        json.GetProperty("collectionsInfo").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Equal("budgets", "entities", "jobs", "paymentmethods", "prices", "printersettings", "tasks", "users");
        json.GetProperty("collectionsInfo").EnumerateArray().Select(c => (c.GetProperty("name").GetString()!, c.GetProperty("count").GetInt64()))
            .Should().Equal(counts.Select(c => (c.Key, c.Value)));
        json.GetProperty("documents").GetInt64().Should().Be(counts.Values.Sum()).And.Be(30_755);
        var stats = await app.Database.RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1), cancellationToken: Cancellation);
        foreach (var (field, statistic) in new[] { ("documents", "objects"), ("indexes", "indexes"), ("avgObjSize", "avgObjSize"), ("dataSize", "dataSize") })
        {
            json.GetProperty(field).GetDouble().Should().Be(stats[statistic].ToDouble(), $"{field} is dbStats.{statistic}");
        }
        json.GetProperty("collectionsInfo").EnumerateArray().Should().OnlyContain(c => c.GetProperty("indexes").GetInt32() == 1
            && c.GetProperty("size").GetDouble() > 0 && c.GetProperty("avgObjSize").GetDouble() > 0
            && c.GetProperty("storageSize").GetDouble() > 0 && c.GetProperty("indexSize").GetDouble() > 0);
    }

    [Fact]
    public async Task Info_of_an_empty_collection_has_avgObjSize_0_and_views_and_system_collections_are_not_listed()
    {
        await app.Database.CreateCollectionAsync("aaa-empty", cancellationToken: Cancellation);
        await app.Database.CreateViewAsync<BsonDocument, BsonDocument>("jobs-view", "jobs", new EmptyPipelineDefinition<BsonDocument>(), cancellationToken: Cancellation);
        try
        {
            using var response = await _client.GetAsync(InfoUri, Cancellation);
            var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Cancellation));

            var rows = json.GetProperty("collectionsInfo").EnumerateArray().ToList();
            rows.Select(r => r.GetProperty("name").GetString()).Should().StartWith(["aaa-empty"]).And.NotContain(n => n == "jobs-view" || n!.StartsWith("system.", StringComparison.Ordinal));
            rows[0].GetProperty("count").GetInt32().Should().Be(0);
            rows[0].GetProperty("size").GetInt32().Should().Be(0);
            rows[0].GetProperty("avgObjSize").GetRawText().Should().Be("0", "legacy `s.avgObjSize || 0`: collStats has none for an empty collection");
            json.GetProperty("collections").GetInt32().Should().Be(9);
        }
        finally
        {
            await app.Database.DropCollectionAsync("jobs-view", Cancellation);
            await app.Database.DropCollectionAsync("aaa-empty", Cancellation);
        }
    }

    [Theory]
    [InlineData("db-info-forbidden", "/api/database/info")]
    [InlineData("db-info-forbidden", "/api/database/backup")]
    public async Task Without_the_settings_role_the_legacy_401(string stepId, string path)
    {
        var expected = (await RecordedStepAsync(stepId)).GetProperty("response");
        using var client = app.CreateClient(TestUser.Limited);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancellation);

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be(expected.GetProperty("text").GetString());
        response.Content.Headers.ContentDisposition.Should().BeNull();
    }

    [Fact]
    public async Task Backup_matches_the_recorded_legacy_step_and_holds_exactly_the_database()
    {
        var step = await RecordedStepAsync("db-backup");
        var expected = step.GetProperty("response");
        var expectedBody = expected.GetProperty("body");
        var before = DateTime.UtcNow.AddSeconds(-1);

        using var response = await _client.GetAsync(BackupUri, Cancellation);
        var file = await response.Content.ReadAsByteArrayAsync(Cancellation);
        var after = DateTime.UtcNow.AddSeconds(1);

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        var disposition = response.Content.Headers.GetValues("Content-Disposition").Single();
        disposition.Should().MatchRegex(Regex.Escape(expected.GetProperty("contentDisposition").GetString()!).Replace(@"\{\{stamp}}", @"\d{4}-\d\d-\d\dT\d\d-\d\d-\d\d-\d{3}Z", StringComparison.Ordinal));
        StampTime(disposition).Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        response.Headers.GetValues("Access-Control-Expose-Headers").Single().Should().Be(expected.GetProperty("accessControlExposeHeaders").GetString());

        file[..2].Should().Equal([0x1f, 0x8b], "gzip");
        var backup = DibbakFile.Parse(file);
        Keys(backup.Header).Should().Equal(Keys(expectedBody.GetProperty("header")));
        backup.Header.GetProperty("format").GetString().Should().Be("dib4t-backup");
        backup.Header.GetProperty("version").GetRawText().Should().Be("1");
        backup.Header.GetProperty("createdBy").GetString().Should().Be(TestUser.Admin.Email, "legacy req.user.email || req.user.name");
        backup.Header.GetProperty("database").GetString().Should().Be(app.Database.DatabaseNamespace.DatabaseName);
        backup.Header.GetProperty("serverVersion").GetString().Should().Be("9.0.8");
        var createdAt = backup.Header.GetProperty("createdAt").GetString()!;
        createdAt.Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        backup.Header.GetProperty("collections").EnumerateArray().Select(c => c.GetString())
            .Should().Equal(expectedBody.GetProperty("header").GetProperty("collections").EnumerateArray().Select(c => c.GetString()));

        var counts = await CountsAsync();
        backup.Collections.Select(c => c.Name).Should().Equal(counts.Keys);
        foreach (var collection in backup.Collections)
        {
            Keys(collection.Meta).Should().Equal("name", "options", "indexes", "count");
            collection.Meta.GetProperty("options").GetRawText().Should().Be("{}");
            // mongo:9.0 lists no "ns" in index specs (the recorded 4.1.4 does); the restore drops it either way.
            collection.Meta.GetProperty("indexes").GetRawText().Should().Be("""[{"v":2,"key":{"_id":1},"name":"_id_"}]""");
            collection.Meta.GetProperty("count").GetInt64().Should().Be(counts[collection.Name]).And.Be(collection.Documents.Count);
        }

        backup.ToSnapshot().DiffFrom(await SnapshotAsync()).Should().BeEmpty("every document, BSON types and field order included");
        await NaturalOrderShouldBeTheFileOrderAsync(backup);
    }

    [Fact]
    public async Task A_backup_restores_into_the_same_database_and_a_second_backup_holds_the_same_documents()
    {
        await app.Database.GetCollection<BsonDocument>("prices").InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "int", 1 }, { "long", 2L }, { "double", 1.0 }, { "decimal", new Decimal128(1.50m) },
            { "date", new BsonDateTime(DateTime.UnixEpoch) }, { "bin", new BsonBinaryData([1, 2, 3]) }, { "nul", BsonNull.Value },
            { "regex", new BsonRegularExpression("^a", "i") }, { "nested", new BsonDocument("a", new BsonArray { 1, "x" }) },
        }, cancellationToken: Cancellation);
        var snapshot = await SnapshotAsync();
        var first = await DownloadAsync();

        using (var content = new ByteArrayContent(first))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var restored = await _client.PostAsync(RestoreUri, content, Cancellation);
            restored.StatusCode.Should().Be(HttpStatusCode.OK);
            (await restored.Content.ReadAsStringAsync(Cancellation)).Should().Contain($"\"collections\":8,\"documents\":{snapshot.DocumentCount}");
        }

        (await SnapshotAsync()).DiffFrom(snapshot).Should().BeEmpty("the restored backup is the database it was made of");
        DibbakFile.Parse(await DownloadAsync()).ToSnapshot().DiffFrom(DibbakFile.Parse(first).ToSnapshot()).Should().BeEmpty();
    }

    [Fact]
    public async Task Indexes_and_collection_options_are_written_as_the_legacy_JSON_and_restore()
    {
        await app.Database.CreateCollectionAsync("capped", new CreateCollectionOptions { Capped = true, MaxSize = 4096 }, Cancellation);
        var jobs = app.Database.GetCollection<BsonDocument>("jobs");
        await jobs.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Descending("startDate"),
            new CreateIndexOptions<BsonDocument> { Name = "start", Sparse = true, ExpireAfter = TimeSpan.FromSeconds(1e9) }), cancellationToken: Cancellation);
        try
        {
            var file = await DownloadAsync();
            var backup = DibbakFile.Parse(file);

            backup.Collections.Select(c => c.Name).Should().StartWith(["budgets", "capped", "entities"], "localeCompare order");
            backup.Collections.Single(c => c.Name == "capped").Meta.GetProperty("options").GetRawText().Should().Be("""{"capped":true,"size":4096}""");
            backup.Collections.Single(c => c.Name == "jobs").Meta.GetProperty("indexes").GetRawText().Should()
                .Be("""[{"v":2,"key":{"_id":1},"name":"_id_"},{"v":2,"key":{"startDate":-1},"name":"start","sparse":true,"expireAfterSeconds":1000000000}]""");

            await app.Database.DropCollectionAsync("capped", Cancellation);
            await jobs.Indexes.DropOneAsync("start", Cancellation);
            using var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var restored = await _client.PostAsync(RestoreUri, content, Cancellation);
            restored.StatusCode.Should().Be(HttpStatusCode.OK);

            var options = (await (await app.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "capped") }, Cancellation))
                .SingleAsync(Cancellation))["options"].AsBsonDocument;
            options.Names.Should().Equal("capped", "size");
            (options["capped"].AsBoolean, options["size"].ToDouble()).Should().Be((true, 4096));
            var indexes = await (await jobs.Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation);
            indexes.Select(i => i["name"].AsString).Should().Equal("_id_", "start");
        }
        finally
        {
            await app.Database.DropCollectionAsync("capped", Cancellation);
            await jobs.Indexes.DropAllAsync(Cancellation);
        }
    }

    /// <summary>
    /// The busy lock (the legacy step left for Phase 5): a backup whose download is not read yet holds it, so a restore
    /// is 409 "(backup)" and the info shows it; once read to the end the lock is free again.
    /// </summary>
    [Fact]
    public async Task A_running_backup_holds_the_lock_against_a_restore_and_a_second_backup()
    {
        using var download = await _client.GetAsync(BackupUri, HttpCompletionOption.ResponseHeadersRead, Cancellation);
        download.StatusCode.Should().Be(HttpStatusCode.OK);

        await BusyShouldBeAsync("backup"); // the server waits for the client to read (the file is larger than the pipe)
        using (var content = new ByteArrayContent(DibbakTestWriter.Valid().ToFile()))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var restore = await _client.PostAsync(RestoreUri, content, Cancellation);
            await ShouldBeLegacyTextAsync(restore, HttpStatusCode.Conflict, "Já existe uma operação em curso (backup). Tente mais tarde.");
        }

        using (var second = await _client.GetAsync(BackupUri, Cancellation))
        {
            await ShouldBeLegacyTextAsync(second, HttpStatusCode.Conflict, "Já existe uma operação em curso (backup). Tente mais tarde.");
            second.Content.Headers.ContentDisposition.Should().BeNull();
        }

        DibbakFile.Parse(await download.Content.ReadAsByteArrayAsync(Cancellation)).DocumentCount.Should().Be(30_755);
        await BusyShouldBeAsync(null);
    }

    [Fact]
    public async Task A_running_restore_holds_the_lock_against_a_backup()
    {
        var operationLock = app.Factory.Services.GetRequiredService<DatabaseOperationLock>();
        using (operationLock.TryAcquire(DatabaseOperationLock.Restore))
        {
            await BusyShouldBeAsync("restauro");
            using var backup = await _client.GetAsync(BackupUri, Cancellation);
            await ShouldBeLegacyTextAsync(backup, HttpStatusCode.Conflict, "Já existe uma operação em curso (restauro). Tente mais tarde.");
        }

        using var free = await _client.GetAsync(BackupUri, Cancellation);
        free.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_failure_before_the_first_byte_is_the_legacy_500_text_and_frees_the_lock()
    {
        await using var factory = WithBackupService(new FailingBackups(bytesBeforeFailure: 0));
        using var client = factory.CreateDefaultClient(new TestUserHandler(TestUser.Admin, app.Database, TestUser.Admin.TokenFrom(factory.Services)));

        using var response = await client.GetAsync(BackupUri, Cancellation);

        await ShouldBeLegacyTextAsync(response, HttpStatusCode.InternalServerError, "Erro ao criar o backup.");
        response.Content.Headers.ContentDisposition.Should().BeNull("not an attachment (legacy keeps the download headers on the text)");
        response.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse();
        factory.Services.GetRequiredService<DatabaseOperationLock>().BusyWith.Should().BeNull();
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath, r => r.Any(x => x.Message == "Database backup failed"), cancellationToken: Cancellation);
        var failed = rows.Last(r => r.Message == "Database backup failed");
        (failed.Level, failed.Category).Should().Be(("Error", "Database"));
        failed.Exception.Should().Contain("disk on fire");
    }

    [Fact]
    public async Task A_failure_after_part_of_the_file_was_sent_aborts_the_download()
    {
        await using var factory = WithBackupService(new FailingBackups(bytesBeforeFailure: 100_000));
        using var client = factory.CreateDefaultClient(new TestUserHandler(TestUser.Admin, app.Database, TestUser.Admin.TokenFrom(factory.Services)));

        var download = async () =>
        {
            using var response = await client.GetAsync(BackupUri, Cancellation);
            return await response.Content.ReadAsByteArrayAsync(Cancellation);
        };

        (await download.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e => e is HttpRequestException || e is IOException);
        factory.Services.GetRequiredService<DatabaseOperationLock>().BusyWith.Should().BeNull();
    }

    [Fact]
    public async Task The_backup_is_logged_with_its_counts()
    {
        var file = DibbakFile.Parse(await DownloadAsync());

        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Message == $"Database backup completed: 8 collections, {file.DocumentCount} documents"), cancellationToken: Cancellation);
        rows.Should().Contain(r => r.Message == $"Database backup started (by {TestUser.Admin.Email})" && r.Category == "Database" && r.Level == "Information");
        rows.Should().Contain(r => r.Message.StartsWith("HTTP GET", StringComparison.Ordinal) && r.Message.Contains("api/database/backup responded 200", StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> WithBackupService(IDatabaseBackupService backups) =>
        app.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddScoped(_ => backups)));

    /// <summary>Writes <c>bytesBeforeFailure</c> bytes (flushed: the response has started when there are any), then throws.</summary>
    private sealed class FailingBackups(int bytesBeforeFailure) : IDatabaseBackupService
    {
        public Task<Result<RestoreSummary>> RestoreAsync(Stream backup, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DatabaseInfo> GetInfoAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<BackupSummary> WriteBackupAsync(Stream output, string? createdBy, DateTime createdAt, CancellationToken cancellationToken)
        {
            if (bytesBeforeFailure > 0)
            {
                await output.WriteAsync(new byte[bytesBeforeFailure], cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            throw new IOException("disk on fire");
        }
    }

    private async Task BusyShouldBeAsync(string? operation)
    {
        using var response = await _client.GetAsync(InfoUri, Cancellation);
        var busy = JsonElement.Parse(await response.Content.ReadAsStringAsync(Cancellation)).GetProperty("busyWith");
        (busy.ValueKind == JsonValueKind.Null ? null : busy.GetString()).Should().Be(operation);
    }

    private static async Task ShouldBeLegacyTextAsync(HttpResponseMessage response, HttpStatusCode status, string text)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be(text);
    }

    private async Task<byte[]> DownloadAsync()
    {
        using var response = await _client.GetAsync(BackupUri, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(Cancellation);
    }

    /// <summary>Documents per user collection, in the legacy order.</summary>
    private async Task<SortedDictionary<string, long>> CountsAsync()
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in await UserCollectionsAsync())
        {
            counts[name] = await app.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Cancellation);
        }

        return counts;
    }

    private async Task<IEnumerable<string>> UserCollectionsAsync() =>
        (await (await app.Database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation))
        .Where(n => !n.StartsWith("system.", StringComparison.Ordinal));

    private async Task<DbSnapshot> SnapshotAsync()
    {
        var collections = new Dictionary<string, IEnumerable<BsonDocument>>();
        foreach (var name in await UserCollectionsAsync())
        {
            collections[name] = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        }

        return DbSnapshot.From(collections);
    }

    /// <summary>The documents of each collection come in the server's natural order (legacy <c>find({})</c>), byte for byte.</summary>
    private async Task NaturalOrderShouldBeTheFileOrderAsync(DibbakFile backup)
    {
        foreach (var collection in backup.Collections)
        {
            var stored = await app.Database.GetCollection<RawBsonDocument>(collection.Name).Find(FilterDefinition<RawBsonDocument>.Empty).ToListAsync(Cancellation);
            stored.Select(d =>
            {
                var bytes = new byte[d.Slice.Length];
                d.Slice.GetBytes(0, bytes, 0, bytes.Length);
                return bytes;
            }).Should().BeEquivalentTo(collection.Documents, o => o.WithStrictOrdering(), collection.Name);
        }
    }

    private static DateTime StampTime(string disposition)
    {
        var stamp = StampPattern().Match(disposition).Groups[1].Value;
        return DateTime.ParseExact(stamp, "yyyy-MM-dd'T'HH-mm-ss-fff'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    [GeneratedRegex(@"dib4t-backup-([0-9T-]+Z)\.dibbak")]
    private static partial Regex StampPattern();

    private static IEnumerable<string> Keys(JsonElement json) => json.EnumerateObject().Select(p => p.Name);

    private static async Task<JsonElement> RecordedStepAsync(string id) =>
        JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization("06-database.json"), Cancellation))
            .GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
}
