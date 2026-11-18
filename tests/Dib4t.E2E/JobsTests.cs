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
