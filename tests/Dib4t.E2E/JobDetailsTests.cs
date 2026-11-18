using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>
/// Job details (PLAN.md §7.3, §10 Phase 4 step 3): in both UIs, on the same seeded machining job, the machining hours
/// and the components typed and left, an observation written, and "Verificação e controlo de forma" ticked OK show the
/// same cost summary and leave the same job document, and nothing else changes.
/// </summary>
public sealed class JobDetailsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    [Fact]
    public async Task Registered_values_an_observation_and_a_check_in_both_uis_save_the_same_job()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var target = DetailsJob(seeded);
        var id = target["_id"].ToString()!;
        try
        {
            var results = new List<(DbSnapshot Snapshot, IReadOnlyList<string> Before, IReadOnlyList<string> After)>();
            foreach (var app in Harness.Apps)
            {
                var session = await OpenAsync(app);
                IJobDetailsPage details = app == Harness.Legacy ? new LegacyJobDetailsPage(session.Page) : new NewJobDetailsPage(session.Page);
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

                await details.GotoAsync(id);
                var before = await details.SummaryAsync();
                await details.RegisterAsync("Maquinação", "7");
                await details.RegisterAsync("Componentes", "250.5");
                await details.CommentsAsync("Nota E2E dos detalhes");
                await details.CheckMachiningOkAsync(0);
                results.Add((await app.SnapshotAsync(Cancellation), before, await details.SummaryAsync()));
            }

            var (legacy, ours) = (results[0], results[1]);
            ours.Before.Should().Equal(legacy.Before, "the cost summary on opening");
            ours.After.Should().Equal(legacy.After, "the cost summary after the changes");
            var saved = ours.Snapshot.Collections["jobs"].Single(j => j["_id"] == target["_id"]);
            (saved["machiningStatistic"]["numberOfHoursUsed"].ToDouble(), saved["components"].ToDouble(), saved["comments"].AsString,
                saved["machiningStatistic"]["verificationAndShapeControl"].AsString).Should().Be((7, 250.5, "Nota E2E dos detalhes", "Ok"));
            ours.Snapshot.DiffFrom(legacy.Snapshot).Should().BeEmpty("the job and every other document are the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation);
        }
    }

    /// <summary>A seeded machining job with its budget, machining tasks and all three statistics (the legacy page needs them).</summary>
    private static BsonDocument DetailsJob(DbSnapshot seed)
    {
        var budgets = seed.Collections["budgets"].Select(b => b["_id"]).ToHashSet();
        var machining = seed.Collections["tasks"].Where(t => t.GetValue("taskType", "").AsString == "machining").Select(t => t["budgetId"]).ToHashSet();
        return seed.Collections["jobs"].First(j => j.GetValue("state", "").AsString == "Maquinação" && budgets.Contains(j["budget"]) && machining.Contains(j["budget"])
            && j.Contains("devStatistic") && j.Contains("machiningStatistic") && j.Contains("assemblyStatistic")
            && j["machiningStatistic"]["verificationAndShapeControl"].AsString != "Ok" && j["components"].ToDouble() != 250.5);
    }
}
