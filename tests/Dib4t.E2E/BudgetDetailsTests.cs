using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.E2E;

/// <summary>
/// Budget details (PLAN.md §7.1, §10 Phase 3 step 2): the same edits made in both UIs (each app on its own database
/// loaded from the same seed backup) — a quantity and a unit price changed, a task added, margin, days and
/// validity changed, the description written, "Concluir", an import — show the same totals, effective margin and
/// color, and messages, and leave the same documents (BSON types and field order included; only the new tasks' ids
/// differ). A locked budget refuses a change with the same warning in both and writes nothing.
/// </summary>
public sealed class BudgetDetailsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    [Fact]
    public async Task Details_edits_in_both_uis_show_the_same_totals_and_messages_and_leave_the_same_documents()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var budgets = seeded.Collections["budgets"];
        var tasks = seeded.Collections["tasks"];
        int TaskCount(BsonDocument b) => tasks.Count(t => t["budgetId"] == b["_id"]);
        var revisions = budgets.GroupBy(b => b["budgetNumber"].AsString).ToDictionary(g => g.Key, g => g.Count());
        // An editable budget (latest revision, not complete, no job) with at least two tasks, a total and a description.
        var edited = budgets.GroupBy(b => b["budgetNumber"].AsString)
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First())
            .Where(b => !b.GetValue("complete", false).AsBoolean && !b.GetValue("hasGenerateJob", false).AsBoolean
                        && b.Contains("total") && b.Contains("description") && TaskCount(b) >= 2)
            .OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal).First();
        // Import source: a number with one revision (findOne without a sort is then the same document in both apps).
        var source = budgets.Where(b => b["budgetNumber"] != edited["budgetNumber"] && revisions[b["budgetNumber"].AsString] == 1
                                        && b.Contains("description") && TaskCount(b) >= 2)
            .OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal).First();
        var id = edited["_id"].ToString()!;
        try
        {
            var results = new List<(string App, List<IReadOnlyList<string>> Totals, List<string> Messages, DbSnapshot Snapshot)>();
            foreach (var app in Harness.Apps)
            {
                var page = await SignInAsync(app);
                await page.GotoAsync(id);
                var totals = new List<IReadOnlyList<string>> { await page.TotalsAsync() };
                var messages = new List<string>();

                await WritesAsync(page.Page, 2, async () => { await page.Quantity(0).FillAsync("3"); await page.Quantity(0).PressAsync("Tab"); });
                await WritesAsync(page.Page, 2, async () => { await page.UnitPrice(1).FillAsync("12.5"); await page.UnitPrice(1).PressAsync("Tab"); });
                var count = await page.Tasks.CountAsync();
                await WritesAsync(page.Page, 2, () => page.AddTaskAsync(0));
                (await page.Tasks.CountAsync()).Should().Be(count + 1, app.Name);
                await WritesAsync(page.Page, 2, async () => { await page.Margin.FillAsync("28"); await page.Margin.PressAsync("Tab"); });
                await WritesAsync(page.Page, 1, async () => { await page.Days.FillAsync("45"); await page.Days.PressAsync("Tab"); });
                await WritesAsync(page.Page, 1, async () => { await page.Validity.FillAsync("15"); await page.Validity.PressAsync("Tab"); });
                totals.Add(await page.TotalsAsync());

                await page.ToggleViewAsync();
                await WritesAsync(page.Page, 1, async () => { await page.Description.FillAsync("Texto E2E\nsegunda linha"); await page.Description.PressAsync("Tab"); });
                messages.Add(await NextNotificationAsync(page, () => WritesAsync(page.Page, 1, page.CompleteAsync)));
                await page.ToggleViewAsync();
                messages.Add(await NextNotificationAsync(page, () => WritesAsync(page.Page, 1, () => page.ImportAsync(source["budgetNumber"].AsString))));

                results.Add((app.Name, totals, messages, await app.SnapshotAsync(Cancellation)));
            }

            var (legacy, ours) = (results[0], results[1]);
            ours.Totals.Should().BeEquivalentTo(legacy.Totals, o => o.WithStrictOrdering());
            ours.Messages.Should().Equal(legacy.Messages).And.Equal("Orçamento completo", "Importado com sucesso");
            Budget(ours.Snapshot, id).Should().Be(Budget(legacy.Snapshot, id), "the edited budget, BSON-for-BSON");
            TasksOf(ours.Snapshot, id).Should().Equal(TasksOf(legacy.Snapshot, id), "the imported copies (ids aside)");
            Others(ours.Snapshot, id).DiffFrom(Others(legacy.Snapshot, id)).Should().BeEmpty("every other document is the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation);
        }
    }

    [Fact]
    public async Task A_locked_budget_refuses_a_change_with_the_same_warning_in_both_uis()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var locked = seeded.Collections["budgets"].GroupBy(b => b["budgetNumber"].AsString)
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First())
            .Where(b => b.GetValue("complete", false).AsBoolean && b.GetValue("wasPdfDownloaded", false).AsBoolean && !b.GetValue("hasGenerateJob", false).AsBoolean
                        && seeded.Collections["tasks"].Any(t => t["budgetId"] == b["_id"]))
            .OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal).First();
        var messages = new List<string>();
        foreach (var app in Harness.Apps)
        {
            var page = await SignInAsync(app);
            await page.GotoAsync(locked["_id"].ToString()!);
            messages.Add(await NextNotificationAsync(page, () => WritesAsync(page.Page, 0, () => page.Margin.PressAsync("5"))));
            (await app.SnapshotAsync(Cancellation)).DiffFrom(seeded).Should().BeEmpty(app.Name + ": nothing written");
        }

        messages.Should().Equal("Oçamento bloqueado", "Oçamento bloqueado");
    }

    /// <summary>Signed in, on a desktop screen: at 1366×768 the legacy header's margin input is not visible (a §8.4 layout bug).</summary>
    private async Task<IBudgetDetailsPage> SignInAsync(IAppUnderTest app)
    {
        var session = await OpenAsync(app, new ViewportSize { Width = 1920, Height = 1080 });
        if (app == Harness.Legacy)
        {
            await new LegacyLoginPage(session.Page).GotoAsync();
            await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
            return new LegacyBudgetDetailsPage(session.Page);
        }

        await new NewLoginPage(session.Page).GotoAsync();
        await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
        return new NewBudgetDetailsPage(session.Page);
    }

    /// <summary>
    /// Runs <paramref name="action"/> and waits until <paramref name="count"/> writes (non-GET API answers) arrived, so
    /// both apps are compared after the same requests; with 0, waits a moment and checks that none was sent.
    /// </summary>
    private static async Task WritesAsync(IPage page, int count, Func<Task> action)
    {
        var seen = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResponse(object? sender, IResponse response)
        {
            if (response.Request.Method != "GET" && response.Url.Contains("/api/", StringComparison.Ordinal) && Interlocked.Increment(ref seen) >= count)
            {
                done.TrySetResult();
            }
        }

        page.Response += OnResponse;
        try
        {
            await action();
            if (count > 0)
            {
                await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                await page.WaitForTimeoutAsync(1000);
                seen.Should().Be(0, "nothing is sent");
            }

            await page.WaitForTimeoutAsync(300); // the page applies the last answer
        }
        finally
        {
            page.Response -= OnResponse;
        }
    }

    private static async Task<string> NextNotificationAsync(IBudgetDetailsPage page, Func<Task> action)
    {
        var before = await page.Notification.CountAsync();
        await action();
        var next = page.Notification.Nth(before);
        await next.WaitForAsync(new() { Timeout = 15_000 });
        return (await next.InnerTextAsync()).Trim();
    }

    private static string Budget(DbSnapshot snapshot, string id) =>
        snapshot.Collections["budgets"].Single(b => b["_id"].ToString() == id).ToJson(Canonical);

    /// <summary>The budget's tasks as a multiset (ids masked, sorted by their text).</summary>
    private static List<string> TasksOf(DbSnapshot snapshot, string id) =>
    [
        .. snapshot.Collections["tasks"].Where(t => t["budgetId"].ToString() == id).Select(t =>
        {
            var copy = t.DeepClone().AsBsonDocument;
            copy["_id"] = "{{id}}";
            return copy.ToJson(Canonical);
        }).Order(StringComparer.Ordinal),
    ];

    private static DbSnapshot Others(DbSnapshot snapshot, string id) => DbSnapshot.From(snapshot.Collections.ToDictionary(
        c => c.Key,
        c => c.Value.Where(d => !(c.Key == "budgets" && d["_id"].ToString() == id) && !(c.Key == "tasks" && d["budgetId"].ToString() == id))));
}
