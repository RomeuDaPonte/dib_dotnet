using System.Net;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Calculations;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 4 step 3 (PLAN.md §4.0 item 3, §11.3) for the job details page, on the production backup (1118 jobs):
/// saving what the page shows unchanged (the registered hours and costs, the order identification) writes nothing,
/// whatever the stored types (Int32/Double hours, the one stored <c>null</c> cost); D17: every job without
/// <c>jobStatisticsDates</c> (124) gets its first state date (legacy: 500); and the cost section computes for every
/// job with its budget's tasks and the stored prices.
/// </summary>
public sealed class ImportedJobDetailsTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unchanged_values_write_nothing_and_d17_dates_the_jobs_without_state_dates()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);
        var stored = seed.Collections["jobs"];

        foreach (var job in stored)
        {
            var id = job["_id"].ToString()!;
            await PatchAsync(client, $"/api/job/register/{id}", new { newRegisteredValue = Raw(job["devStatistic"]["numberOfHoursUsed"]), taskType = "development" });
            await PatchAsync(client, $"/api/job/register/{id}", new { newRegisteredValue = Raw(job.GetValue("costNonQuality", 0)), taskType = "custoNaoQualidade" });
            await PatchAsync(client, $"/api/job/register/{id}", new { newRegisteredValue = Raw(job.GetValue("materials", 0)), taskType = "materials" });
            await PatchAsync(client, $"/api/job/singleProperty/{id}", new { propertyName = "orderIdentification", value = job.GetValue("orderIdentification", "Por preencher").AsString });
        }

        var jobs = app.Database.GetCollection<BsonDocument>("jobs");
        var after = await jobs.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        after.Select(d => d.ToJson()).Should().BeEquivalentTo(stored.Select(d => d.ToJson()), "unchanged values write nothing");

        var undated = stored.Where(j => !j.Contains("jobStatisticsDates")).ToList();
        undated.Should().HaveCount(124);
        foreach (var job in undated)
        {
            await PatchAsync(client, $"/api/job/statisticsDates/{job["_id"]}", new { forJobState = "devEndedInDate", newDate = "2026-03-10" });
        }

        var dated = await jobs.Find(Builders<BsonDocument>.Filter.In("_id", undated.Select(j => j["_id"]))).ToListAsync(Cancellation);
        dated.Should().HaveCount(124).And.OnlyContain(j => j["jobStatisticsDates"].AsBsonDocument.Names.SequenceEqual(new[] { "_id", "devEndedInDate" })
            && j["jobStatisticsDates"]["devEndedInDate"].ToUniversalTime() == new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
        foreach (var (before, now) in undated.Zip(dated.OrderBy(d => undated.FindIndex(u => u["_id"] == d["_id"]))))
        {
            var rest = now.DeepClone().AsBsonDocument;
            rest.Remove("jobStatisticsDates");
            rest.ToJson().Should().Be(before.ToJson(), "D17 adds only the state dates");
        }
    }

    [Fact]
    public void The_cost_section_computes_for_every_imported_job()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        var budgets = seed.Collections["budgets"].ToDictionary(b => b["_id"], b => b);
        var tasks = seed.Collections["tasks"].ToLookup(t => t.GetValue("budgetId", BsonNull.Value));
        var stored = seed.Collections["prices"].Single();
        var prices = new[] { "automation", "consultancy", "development", "machining", "assembly" }
            .Where(stored.Contains).ToDictionary(t => t, t => stored[t].ToDouble(), StringComparer.Ordinal);
        var computed = 0;
        foreach (var job in seed.Collections["jobs"])
        {
            var budget = budgets.GetValueOrDefault(job["budget"]);
            var page = JobCostCalculations.Page(
                [.. tasks[job["budget"]].Select(t => new CostTask(t.GetValue("taskType", BsonNull.Value) is { IsString: true } type ? type.AsString : null,
                    Number(t, "quantity"), Number(t, "unitPrice")))],
                new JobCostValues(Hours(job, "devStatistic"), Hours(job, "machiningStatistic"), Hours(job, "assemblyStatistic"), Value(job, "components"),
                    Value(job, "automation"), Value(job, "materials"), Value(job, "others"), Value(job, "costNonQuality")),
                budget is null ? double.NaN : Number(budget, "total"),
                prices);
            page.Rows.Should().HaveCount(6);
            var texts = new[] { page.Summary.TasksTotal, page.Summary.Registered, page.Summary.Deviation.Text }.Concat(page.Rows.Select(r => r.Deviation));
            texts.Should().NotContain(t => t.Contains("NaN", StringComparison.Ordinal), $"job {job["_id"]}");
            computed++;
        }

        computed.Should().Be(1118);
    }

    private static double? Raw(BsonValue value) => value.IsBsonNull ? null : value.ToDouble();

    private static double Number(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToDouble() : double.NaN;

    private static JsValue Value(BsonDocument document, string field) =>
        !document.TryGetValue(field, out var value) ? JsValue.Of(0) : value.IsNumeric ? JsValue.Of(value.ToDouble()) : JsValue.Null; // missing: the schema default 0

    private static JsValue Hours(BsonDocument job, string statistic) =>
        job.TryGetValue(statistic, out var s) && s.IsBsonDocument ? Value(s.AsBsonDocument, "numberOfHoursUsed") : JsValue.Undefined;

    private static async Task PatchAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path}: {await response.Content.ReadAsStringAsync(Cancellation)}");
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
