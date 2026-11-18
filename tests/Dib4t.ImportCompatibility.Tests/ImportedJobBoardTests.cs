using System.Net;
using System.Text.Json;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 4 step 2 (PLAN.md §4.0 item 3, §11.3) for the jobs board: after the production backup is imported, the board's
/// reads give every job once, in the legacy order (due date, then priority "Alta", "Média", the rest; "Faturada" and
/// "Para faturação" by date only), with the counters of the stored states; stored <c>null</c>s stay <c>null</c>,
/// missing fields stay missing and the unknown <c>isHightPriority</c> of old jobs comes along; and reading changes
/// nothing in the database.
/// </summary>
public sealed class ImportedJobBoardTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly string[] States =
        ["Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação", "Faturada", "Apagada"];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_board_reads_every_imported_job_in_the_legacy_order_and_changes_nothing()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);
        var stored = seed.Collections["jobs"];

        var all = await GetArrayAsync(client, "/api/job/getAll");
        all.Select(j => j.GetProperty("_id").GetString()).Should().BeEquivalentTo(stored.Select(j => j["_id"].ToString()));
        ShouldBeByDateThenPriority(all, byPriority: true);

        var total = 0;
        foreach (var state in States)
        {
            var list = await GetArrayAsync(client, "/api/job/getByState/" + Uri.EscapeDataString(state));
            list.Should().OnlyContain(j => j.GetProperty("state").GetString() == state);
            list.Count.Should().Be(stored.Count(j => j.GetValue("state", "Desenvolvimento").AsString == state), state);
            ShouldBeByDateThenPriority(list, byPriority: state is not ("Faturada" or "Para faturação"));
            total += list.Count;
        }

        total.Should().Be(stored.Count);
        var unfinished = await GetArrayAsync(client, "/api/job/getUnfinished");
        unfinished.Count.Should().Be(stored.Count(j => j["state"].AsString is not ("Faturada" or "Apagada")));
        var counts = JsonElement.Parse(await GetTextAsync(client, "/api/job/getCounts"));
        counts.GetProperty("allCount").GetInt32().Should().Be(unfinished.Count);
        counts.GetProperty("machiningCount").GetInt32().Should().Be(stored.Count(j => j["state"].AsString == "Maquinação"));

        all.Count(j => j.TryGetProperty("deliveryDate", out var d) && d.ValueKind == JsonValueKind.Null)
            .Should().Be(stored.Count(j => j.TryGetValue("deliveryDate", out var d) && d.IsBsonNull), "stored nulls are answered null");
        all.Count(j => !j.TryGetProperty("deliveryDate", out _)).Should().Be(stored.Count(j => !j.Contains("deliveryDate")), "missing stays missing");
        all.Count(j => j.TryGetProperty("isHightPriority", out _)).Should().Be(stored.Count(j => j.Contains("isHightPriority")));

        var after = await app.Database.GetCollection<BsonDocument>("jobs").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        after.Select(d => d.ToJson()).Should().BeEquivalentTo(stored.Select(d => d.ToJson()), "reading writes nothing");
    }

    private static void ShouldBeByDateThenPriority(IReadOnlyList<JsonElement> jobs, bool byPriority)
    {
        static int Bucket(JsonElement job) => job.TryGetProperty("priority", out var p) ? p.GetString() switch { "Alta" => 0, "Média" => 1, _ => 2 } : 2;
        static DateTime Due(JsonElement job) => job.TryGetProperty("dueDate", out var d) ? d.GetDateTime() : DateTime.MinValue;
        var keys = jobs.Select(j => (Bucket: byPriority ? Bucket(j) : 0, Due: Due(j))).ToList();
        keys.Should().BeInAscendingOrder(Comparer<(int Bucket, DateTime Due)>.Default);
    }

    private static async Task<IReadOnlyList<JsonElement>> GetArrayAsync(HttpClient client, string path) =>
        [.. JsonElement.Parse(await GetTextAsync(client, path)).EnumerateArray()];

    private static async Task<string> GetTextAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancellation);
        var text = await response.Content.ReadAsStringAsync(Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path}: {text}");
        return text;
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
