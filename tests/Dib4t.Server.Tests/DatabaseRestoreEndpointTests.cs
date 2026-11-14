using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dib4t.Application.Database;
using Dib4t.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests;

/// <summary><c>POST /api/database/restore</c> (PLAN.md §6, §7.6) against the legacy golden file and a real mongo:9.0.8.</summary>
public sealed class DatabaseRestoreEndpointTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly Uri RestoreUri = new("/api/database/restore", UriKind.Relative);

    private readonly HttpClient _client = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string> RecordedRestoreSteps => ["db-restore-empty", "db-restore-garbage", "db-restore-truncated", "db-restore"];

    /// <summary>
    /// Replays the recorded legacy steps (<c>characterization/06-database.json</c>): same request kind, same status,
    /// content type and text. The success body is compared by shape (the recorded counts are of the recorder's DB).
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordedRestoreSteps))]
    public async Task Response_matches_the_recorded_legacy_step(string stepId)
    {
        var step = await RecordedStepAsync(stepId);
        var backup = await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation);
        var body = stepId switch
        {
            "db-restore-empty" => [],
            "db-restore-garbage" => "this is not a backup"u8.ToArray(),
            "db-restore-truncated" => backup[..(backup.Length / 2)],
            _ => backup,
        };
        var expected = step.GetProperty("response");

        using var response = await PostAsync(body);
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        if (expected.TryGetProperty("text", out var expectedText))
        {
            text.Should().Be(expectedText.GetString());
        }
        else
        {
            var json = JsonElement.Parse(text);
            json.EnumerateObject().Select(p => p.Name).Should().Equal(expected.GetProperty("json").EnumerateObject().Select(p => p.Name));
            json.GetProperty("backupDatabase").GetString().Should().Be("dib4t");
            json.GetProperty("collections").GetInt32().Should().Be(8);
            json.GetProperty("documents").GetInt32().Should().Be(30_754);
        }
    }

    [Fact]
    public async Task Success_replaces_the_database_and_echoes_the_backup_header()
    {
        var document = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "price", 10 }, { "total", 10.5 } };
        var file = DibbakTestWriter.Valid(("budgets", [document]), ("users", [])).ToFile();

        using var response = await PostAsync(file);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Cancellation)).Should()
            .Be("""{"backupCreatedAt":"2026-11-11T16:15:31.123Z","backupDatabase":"dib4t","collections":2,"documents":1}""");
        (await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .Should().Equal(document);
    }

    [Fact]
    public async Task Header_values_missing_from_the_backup_are_left_out_like_legacy()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame("""{"format":"dib4t-backup","version":1,"database":null}""").EndFrame(0, 0).ToFile();

        using var response = await PostAsync(file);

        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be("""{"backupDatabase":null,"collections":0,"documents":0}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/gzip")]
    [InlineData("text/plain")]
    public async Task Body_of_another_content_type_counts_as_no_file(string? contentType)
    {
        using var content = new ByteArrayContent(DibbakTestWriter.Valid().ToFile());
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);

        using var response = await _client.PostAsync(RestoreUri, content, Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be("Nenhum ficheiro de backup recebido.");
    }

    [Fact]
    public async Task Content_type_matches_case_insensitively_with_parameters()
    {
        using var content = new ByteArrayContent(DibbakTestWriter.Valid().ToFile());
        content.Headers.TryAddWithoutValidation("Content-Type", "Application/Octet-Stream; charset=binary");

        using var response = await _client.PostAsync(RestoreUri, content, Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Route_is_case_insensitive_like_Express()
    {
        using var response = await PostAsync(DibbakTestWriter.Valid().ToFile(), "/API/Database/RESTORE");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Restore_while_a_backup_runs_is_409()
    {
        using var busy = app.Factory.Services.GetRequiredService<DatabaseOperationLock>().TryAcquire(DatabaseOperationLock.Backup);

        using var response = await PostAsync(DibbakTestWriter.Valid().ToFile());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be("Já existe uma operação em curso (backup). Tente mais tarde.");
    }

    [Fact]
    public async Task Rejected_file_leaves_the_database_unchanged()
    {
        await PostAsync(DibbakTestWriter.Valid(("kept", [new BsonDocument("_id", 1)])).ToFile());

        using var response = await PostAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("other").EndFrame(1, 1).ToFile());

        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be("O ficheiro de backup está corrompido (contagens não coincidem).");
        (await (await app.Database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation)).Should().Equal("kept");
    }

    [Fact]
    public async Task Restore_is_blocked_in_production_until_auth_exists()
    {
        // TEMPORARY guard (BlockedInProductionUntilAuthAttribute) until the auth step adds the settings-role policy.
        await using var production = app.Factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();
        using var content = new ByteArrayContent(DibbakTestWriter.Valid().ToFile());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.PostAsync(RestoreUri, content, Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be("Access denied. No token provided.");
    }

    private async Task<HttpResponseMessage> PostAsync(byte[] body, string path = "/api/database/restore")
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(new Uri(path, UriKind.Relative), content, Cancellation);
    }

    private static async Task<JsonElement> RecordedStepAsync(string id)
    {
        var steps = JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization("06-database.json"), Cancellation));
        var list = steps.ValueKind == JsonValueKind.Array ? steps : steps.GetProperty("steps");
        return list.EnumerateArray().Single(step => step.GetProperty("id").GetString() == id);
    }
}
