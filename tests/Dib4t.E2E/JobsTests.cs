using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.E2E;

/// <summary>
/// Job create (PLAN.md §7.3, §10 Phase 4 step 1): "Criar obra" on the same budget in both UIs, each app on its own
/// database loaded from the same seed backup, leaves the same documents: the budget gets <c>hasGenerateJob</c>, and
/// one job with the same fields, types and order (its ids, its start date and the creator's sub-document ids differ).
/// Neither app runs in Production here, so no NAS or printer is involved (those are the golden replays in
/// <c>Dib4t.Server.Tests</c>).
/// </summary>
public sealed class JobsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    [Fact]
    public async Task Criar_obra_in_both_uis_marks_the_budget_and_saves_the_same_job()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        // The latest revision of a number without a job, complete with its PDF (the menu offers "Criar obra"), with tasks.
        var withTasks = seeded.Collections["tasks"].Select(t => t["budgetId"].AsObjectId).ToHashSet();
        var budget = seeded.Collections["budgets"].GroupBy(b => b["budgetNumber"].AsString)
            .Where(g => !g.Any(b => b.GetValue("hasGenerateJob", false).AsBoolean))
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First())
            .Where(b => b.GetValue("complete", false).AsBoolean && b.GetValue("wasPdfDownloaded", false).AsBoolean && withTasks.Contains(b["_id"].AsObjectId))
            .OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal).First();
        var (id, number, revision) = (budget["_id"].AsObjectId, budget["budgetNumber"].AsString, budget.GetValue("revision", 0).ToDouble());
        var job = new JobValues("2030-01-15", "Em Aprovação", "ENC E2E 7", IsOfficial: true);
        try
        {
            var results = new List<(string App, DbSnapshot Snapshot)>();
            foreach (var app in Harness.Apps)
            {
                var session = await OpenAsync(app);
                IBudgetsPage budgets = app == Harness.Legacy ? new LegacyBudgetsPage(session.Page) : new NewBudgetsPage(session.Page);
                if (app == Harness.Legacy)
                {
                    await new LegacyLoginPage(session.Page).GotoAsync();
                    await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }
                else
                {
                    await new NewLoginPage(session.Page).GotoAsync();
                    await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }

                await budgets.GotoAsync();
                await budgets.CreateJobAsync(number, revision, job);
                results.Add((app.Name, await app.SnapshotAsync(Cancellation)));
            }

            var (legacy, ours) = (results[0].Snapshot, results[1].Snapshot);
            Jobs(ours, seeded).Should().Equal(Jobs(legacy, seeded)).And.ContainSingle();
            var created = ours.Collections["jobs"].Single(j => !seeded.Collections["jobs"].Any(s => s["_id"] == j["_id"]));
            (created["budget"].AsObjectId, created["state"].AsString, created["orderIdentification"].AsString).Should().Be((id, "Em Aprovação", "ENC E2E 7"));
            created["dueDate"].ToUniversalTime().Should().Be(new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc));
            ours.Collections["budgets"].Single(b => b["_id"] == id)["hasGenerateJob"].AsBoolean.Should().BeTrue();
            Without(ours, seeded).DiffFrom(Without(legacy, seeded)).Should().BeEmpty("the budget's flag and every other document are the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation); // back to the seed for the other tests
        }
    }

    /// <summary>
    /// Jobs board (PLAN.md §7.3, §10 Phase 4 step 2): in both UIs, on the same seeded development job, "Em Aprovação"
    /// through the statistics dialog with the hours typed, then "Não prioritária" in the approval tab, leave the same job
    /// document (its new statistic's id and the end date stamped now aside) and nothing else changed.
    /// </summary>
    [Fact]
    public async Task A_state_change_and_a_priority_in_both_uis_save_the_same_job()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var target = BoardJob(seeded);
        var number = seeded.Collections["budgets"].Single(b => b["_id"] == target["budget"])["budgetNumber"].AsString;
        var hours = "1";
        try
        {
            var results = new List<DbSnapshot>();
            foreach (var app in Harness.Apps)
            {
                var session = await OpenAsync(app);
                IJobsPage jobs = app == Harness.Legacy ? new LegacyJobsPage(session.Page) : new NewJobsPage(session.Page);
                if (app == Harness.Legacy)
                {
                    await new LegacyLoginPage(session.Page).GotoAsync();
                    await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }
                else
                {
                    await new NewLoginPage(session.Page).GotoAsync();
                    await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }

                await jobs.GotoAsync("Desenvolvimento");
                await jobs.ChangeStateAsync(number, "Em Aprovação", hours);
                await jobs.GotoAsync("Em aprovação");
                await jobs.ChangePriorityAsync(number, "Não prioritária");
                results.Add(await app.SnapshotAsync(Cancellation));
            }

            var (legacy, ours) = (results[0], results[1]);
            var saved = ours.Collections["jobs"].Single(j => j["_id"] == target["_id"]);
            (saved["state"].AsString, saved["priority"].AsString, saved["devStatistic"]["numberOfHoursUsed"].ToDouble()).Should().Be(("Em Aprovação", "Baixa", 1));
            Board(ours, target).Should().Be(Board(legacy, target));
            Except(ours, target["_id"]).DiffFrom(Except(legacy, target["_id"])).Should().BeEmpty("every other document is the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation);
        }
    }

    /// <summary>
    /// Label print (PLAN.md §5, §10 Phase 4 step 4): in both UIs, "Imprimir" in the options menu of the same seeded job,
    /// "Produção" unticked, sends the same body to <c>POST /api/job/print/:budgetId</c> of the job's budget. Neither app
    /// runs in Production, so both answer the legacy 400 "Só funciona em produção!", show it as the error toast, close
    /// the dialog and change nothing (the printing itself is the golden replay in <c>Dib4t.Server.Tests</c>).
    /// </summary>
    [Fact]
    public async Task Imprimir_in_both_uis_sends_the_same_request_and_shows_the_same_answer()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var target = BoardJob(seeded);
        var number = seeded.Collections["budgets"].Single(b => b["_id"] == target["budget"])["budgetNumber"].AsString;
        var outcomes = new List<PrintOutcome>();
        var snapshots = new List<DbSnapshot>();
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            IJobsPage jobs = app == Harness.Legacy ? new LegacyJobsPage(session.Page) : new NewJobsPage(session.Page);
            if (app == Harness.Legacy)
            {
                await new LegacyLoginPage(session.Page).GotoAsync();
                await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            }
            else
            {
                await new NewLoginPage(session.Page).GotoAsync();
                await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            }

            await jobs.GotoAsync("Desenvolvimento");
            outcomes.Add(await jobs.PrintLabelsAsync(number, production: false, machining: true));
            snapshots.Add(await app.SnapshotAsync(Cancellation));
        }

        outcomes[1].Should().Be(outcomes[0]);
        outcomes[1].Should().Be(new PrintOutcome("""{"maquinacao":true,"producao":false}""", 400, "Só funciona em produção!", "Só funciona em produção!"));
        snapshots[1].DiffFrom(seeded).Should().BeEmpty("nothing is written");
        snapshots[0].DiffFrom(seeded).Should().BeEmpty();
    }

    /// <summary>
    /// A seeded development job on the first page of both tabs it is used in, whose budgeted development hours are its
    /// budget's (so the deviation the dialog computes is the one the server checks).
    /// </summary>
    private static BsonDocument BoardJob(DbSnapshot seed)
    {
        var budgets = seed.Collections["budgets"].ToDictionary(b => b["_id"]);
        var tasks = seed.Collections["tasks"].Where(t => t.GetValue("taskType", "").AsString == "development")
            .GroupBy(t => t["budgetId"]).ToDictionary(g => g.Key, g => g.Aggregate(0.0, (sum, t) => sum + t["quantity"].ToDouble()));
        static int Bucket(BsonDocument j) => j.GetValue("priority", BsonNull.Value).ToString() switch { "Alta" => 0, "Média" => 1, _ => 2 };
        List<BsonDocument> Board(string state) =>
            [.. seed.Collections["jobs"].Where(j => j.GetValue("state", "").AsString == state).OrderBy(Bucket).ThenBy(j => j["dueDate"].ToUniversalTime())];
        var development = Board("Desenvolvimento");
        var approval = Board("Em Aprovação");
        return development.Take(25).First(j =>
            j.Contains("devStatistic") && budgets.ContainsKey(j["budget"]) && j.GetValue("priority", "").ToString() != "Baixa"
            && tasks.TryGetValue(j["budget"], out var sum) && sum > 1 && sum == j["devStatistic"]["budgetedHours"].ToDouble()
            && approval.Count(a => Bucket(a) < 2 || a["dueDate"].ToUniversalTime() <= j["dueDate"].ToUniversalTime()) < 25);
    }

    private static DbSnapshot Except(DbSnapshot snapshot, BsonValue jobId) =>
        DbSnapshot.From(snapshot.Collections.ToDictionary(c => c.Key, c => c.Value.Where(d => c.Key != "jobs" || d["_id"] != jobId)));

    /// <summary>The job without the new statistic's id and the end date stamped now.</summary>
    private static string Board(DbSnapshot snapshot, BsonDocument target)
    {
        var copy = snapshot.Collections["jobs"].Single(j => j["_id"] == target["_id"]).DeepClone().AsBsonDocument;
        copy["devStatistic"]["_id"] = "{{id}}";
        if (copy.TryGetValue("jobStatisticsDates", out var dates))
        {
            foreach (var field in new[] { "_id", "devEndedInDate" })
            {
                if (dates.AsBsonDocument.Contains(field) && (field != "_id" || !target.Contains("jobStatisticsDates")))
                {
                    dates[field] = "{{now}}";
                }
            }
        }

        return copy.ToJson(Canonical);
    }

    /// <summary>The new jobs without their generated ids and start date.</summary>
    private static List<string> Jobs(DbSnapshot snapshot, DbSnapshot seed)
    {
        var known = seed.Collections["jobs"].Select(j => j["_id"]).ToHashSet();
        return
        [
            .. snapshot.Collections["jobs"].Where(j => !known.Contains(j["_id"])).Select(j =>
            {
                var copy = j.DeepClone().AsBsonDocument;
                copy["_id"] = "{{id}}";
                copy["startDate"] = "{{now}}";
                foreach (var statistic in new[] { "devStatistic", "machiningStatistic", "assemblyStatistic" })
                {
                    copy[statistic]["_id"] = "{{id}}";
                }

                return copy.ToJson(Canonical);
            }),
        ];
    }

    private static DbSnapshot Without(DbSnapshot snapshot, DbSnapshot seed)
    {
        var known = seed.Collections["jobs"].Select(j => j["_id"]).ToHashSet();
        return DbSnapshot.From(snapshot.Collections.ToDictionary(c => c.Key, c => c.Value.Where(d => c.Key != "jobs" || known.Contains(d["_id"]))));
    }
}
