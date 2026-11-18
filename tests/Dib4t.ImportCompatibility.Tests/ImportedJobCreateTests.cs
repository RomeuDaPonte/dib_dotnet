using System.Net;
using System.Text.Json;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 4 step 1 (PLAN.md §4.0 item 3, §11.3) for job create: after the production backup is imported, a job is
/// created from the latest revision of <b>every</b> number that has none (the "Criar obra" of the list), and every
/// number that has one refuses a second (legacy text). In the whole database only this changes: those budgets get
/// <c>hasGenerateJob: true</c> (in place, or added at the end when missing), and one job per number is inserted with
/// the legacy defaults and the budgeted hours of its budget's tasks (summed as JavaScript does, Int32/Double as the
/// legacy driver writes them). Imported jobs, tasks and every other collection are untouched.
/// </summary>
public sealed class ImportedJobCreateTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_number_without_a_job_gets_one_and_nothing_else_changes()
    {
        var file = DibbakFile.Read(Fixtures.ProductionBackup);
        var seed = file.ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        var budgets = seed.Collections["budgets"];
        var withJob = budgets.Where(b => b.GetValue("hasGenerateJob", false).AsBoolean).Select(b => b["budgetNumber"].AsString).ToHashSet(StringComparer.Ordinal);
        var latest = budgets.GroupBy(b => b["budgetNumber"].AsString, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First()).ToList();
        // The file's order is legacy's natural order (its find() wrote it), which find({ budgetId }) returns after the
        // restore too; the sums depend on it (0.5 + 0.3 + 0.9 + … ≠ the same numbers in another order).
        var tasksByBudget = file.Collections.Single(c => c.Name == "tasks").Documents.Select(d => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(d))
            .GroupBy(t => t["budgetId"].AsObjectId).ToDictionary(g => g.Key, g => g.ToList());
        var created = new Dictionary<ObjectId, BsonDocument>();
        var (refused, statuses) = (0, new Dictionary<string, int>(StringComparer.Ordinal));
        foreach (var budget in latest)
        {
            var number = budget["budgetNumber"].AsString;
            using var response = await client.PostAsync(new Uri("/api/job", UriKind.Relative), Json(new
            {
                budgetId = budget["_id"].ToString(),
                finalPrice = 1000,
                dueDate = "2027-02-01",
                state = "Desenvolvimento",
                orderIdentification = "PO-" + number,
                isOfficialOrderIdentification = false,
            }), Cancellation);
            var text = await response.Content.ReadAsStringAsync(Cancellation);
            var key = $"{(int)response.StatusCode} {(response.StatusCode == HttpStatusCode.OK ? "" : text)}";
            statuses[key] = statuses.GetValueOrDefault(key) + 1;
            if (withJob.Contains(number))
            {
                (response.StatusCode, text).Should().Be((HttpStatusCode.BadRequest, "Obra baseada neste orçamento já foi criada anteriormente"), number);
                refused++;
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{number}: {text}");
            JsonElement.Parse(text).GetProperty("budget").GetProperty("hasGenerateJob").GetBoolean().Should().BeTrue();
            created[budget["_id"].AsObjectId] = budget;
        }

        (refused, created.Count).Should().Be((withJob.Count, latest.Count - withJob.Count), string.Join(", ", statuses.Select(s => $"{s.Key}: {s.Value}")));
        created.Count.Should().BeGreaterThan(1500, "the backup has about 2,300 numbers, 1,118 with a job");

        var afterBudgets = await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        afterBudgets.Select(b => b.ToJson()).Should().BeEquivalentTo(budgets.Select(b =>
        {
            var copy = b.DeepClone().AsBsonDocument;
            if (created.ContainsKey(copy["_id"].AsObjectId))
            {
                copy.Set("hasGenerateJob", true); // in place, or added at the end (Mongoose $set of a missing path)
            }

            return copy.ToJson();
        }), "only hasGenerateJob of the budgets the jobs were made from");

        var jobs = await app.Database.GetCollection<BsonDocument>("jobs").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        var imported = seed.Collections["jobs"].Select(j => j["_id"].AsObjectId).ToHashSet();
        jobs.Where(j => imported.Contains(j["_id"].AsObjectId)).Select(j => j.ToJson())
            .Should().BeEquivalentTo(seed.Collections["jobs"].Select(j => j.ToJson()), "the imported jobs are untouched");
        var added = jobs.Where(j => !imported.Contains(j["_id"].AsObjectId)).ToList();
        added.Should().HaveCount(created.Count);
        foreach (var job in added)
        {
            var budgetId = job["budget"].AsObjectId;
            created.Should().ContainKey(budgetId);
            var own = tasksByBudget.GetValueOrDefault(budgetId) ?? [];
            job.Names.Should().Equal("_id", "invoicedPercent", "finalPrice", "state", "orderIdentification", "isOfficialOrderIdentification", "components",
                "automation", "materials", "others", "costNonQuality", "commentType", "budget", "createdBy", "dueDate", "startDate", "devStatistic",
                "machiningStatistic", "assemblyStatistic", "priority", "__v");
            (job["createdBy"].AsString, job["priority"].AsString, job["orderIdentification"].AsString).Should().Be(
                ("Ana Administradora", "Média", "PO-" + created[budgetId]["budgetNumber"].AsString));
            foreach (var (field, type) in new[] { ("devStatistic", "development"), ("machiningStatistic", "machining"), ("assemblyStatistic", "assembly") })
            {
                var hours = own.Where(t => t.GetValue("taskType", BsonNull.Value) == type).Aggregate(0.0, (sum, t) => sum + t["quantity"].ToDouble());
                var statistic = job[field].AsBsonDocument;
                statistic["budgetedHours"].Should().Be(JsNumber(hours), $"{field} of {created[budgetId]["budgetNumber"]}");
                statistic["deviation"].Should().Be(JsNumber(hours));
            }
        }

        foreach (var name in seed.Collections.Keys.Where(n => n is not "budgets" and not "jobs" and not "users"))
        {
            var documents = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            documents.Select(d => d.ToJson()).Should().BeEquivalentTo(seed.Collections[name].Select(d => d.ToJson()), name + " is not touched");
        }
    }

    /// <summary>The legacy driver's number rule (PLAN.md §4.3 rule 1).</summary>
    private static BsonValue JsNumber(double value) =>
        value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)value) : new BsonDouble(value);

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
