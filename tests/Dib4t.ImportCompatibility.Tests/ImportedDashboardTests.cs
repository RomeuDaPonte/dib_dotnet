using System.Net;
using System.Text.Json;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 5 step 1 (PLAN.md §4.0 item 3, §11.3) for the dashboard: after the production backup is imported, the
/// counters of <b>every</b> year from the first to the last job start (and the years around them) are what an
/// independent count over the backup's own documents gives for the whole year in Europe/Lisbon (D10): the jobs in
/// "Para faturação" or "Faturada" whose <c>startDate</c> is a date in that local year, by the text of
/// <c>assemblyStatistic.validTryOnFirstTest</c> (1055 jobs, 2022–2026). Legacy's range (UTC midnights, 31 December
/// after 00:00 in no year) gives the same counters on this backup: no job starts in the hours D10 moves. Nothing in
/// the database changes.
/// </summary>
public sealed class ImportedDashboardTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_year_counts_the_whole_Lisbon_year_of_the_backup()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        var counted = seed.Collections["jobs"]
            .Where(j => j.GetValue("state", BsonNull.Value) is BsonString { Value: "Para faturação" or "Faturada" })
            .Where(j => j.GetValue("startDate", BsonNull.Value).IsValidDateTime)
            .Select(j => (Start: j["startDate"].ToUniversalTime(), Value: Value(j)))
            .ToList();
        var years = counted.Select(j => j.Start.Year).ToList();
        var (first, last) = (years.Min() - 1, years.Max() + 1);

        var total = 0;
        var movedByD10 = 0;
        for (var year = first; year <= last; year++)
        {
            using var response = await client.GetAsync(new Uri($"/api/job/getValidTryOnFirstTestCountersByYear/{year}", UriKind.Relative), Cancellation);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Cancellation));
            var answer = (json.GetProperty("oneCounter").GetInt32(), json.GetProperty("twoCounter").GetInt32(), json.GetProperty("threeCounter").GetInt32());

            var expected = Counters(counted.Where(j => TimeZoneInfo.ConvertTimeFromUtc(j.Start, Lisbon).Year == year));
            answer.Should().Be(expected, $"the year {year} in Europe/Lisbon");

            var legacyFrom = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var legacy = Counters(counted.Where(j => j.Start >= legacyFrom && j.Start <= legacyFrom.AddYears(1).AddDays(-1)));
            movedByD10 += Math.Abs(answer.Item1 - legacy.Item1) + Math.Abs(answer.Item2 - legacy.Item2) + Math.Abs(answer.Item3 - legacy.Item3);
            total += answer.Item1 + answer.Item2 + answer.Item3;
        }

        total.Should().Be(counted.Count(j => j.Value is "1" or "2" or "3"), "every counted job is in exactly one year");
        (total, first + 1, last - 1).Should().Be((1055, 2022, 2026), "the backup's jobs in Para faturação/Faturada start in 2022–2026");
        movedByD10.Should().Be(0, "no job of the backup starts on 31 December after 00:00 UTC: D10 changes no counter of the backup");

        foreach (var (name, documents) in seed.Collections.Where(c => c.Key != "users"))
        {
            var after = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            after.Select(d => d.ToJson()).Should().BeEquivalentTo(documents.Select(d => d.ToJson()), name + " is not touched");
        }
    }

    /// <summary>The stored value as MongoDB's equality with a text sees it (a number or a missing statistic is none of "1", "2", "3").</summary>
    private static string? Value(BsonDocument job) =>
        job.GetValue("assemblyStatistic", BsonNull.Value) is BsonDocument statistic && statistic.GetValue("validTryOnFirstTest", BsonNull.Value) is BsonString text
            ? text.Value
            : null;

    private static (int, int, int) Counters(IEnumerable<(DateTime Start, string? Value)> jobs)
    {
        var list = jobs.ToList();
        return (list.Count(j => j.Value == "1"), list.Count(j => j.Value == "2"), list.Count(j => j.Value == "3"));
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
