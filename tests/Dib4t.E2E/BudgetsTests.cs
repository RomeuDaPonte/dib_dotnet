using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.E2E;

/// <summary>
/// Budget list (PLAN.md §7.1, §10 Phase 3 step 1): the same budget created, cloned and an existing one given a new
/// revision through both UIs, each app on its own database loaded from the same seed backup, show the same messages
/// and rows and leave the same documents (BSON types and field order included; ids and creation dates differ).
/// </summary>
public sealed class BudgetsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    [Fact]
    public async Task Budget_changes_in_both_uis_show_the_same_messages_and_leave_the_same_documents()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var seedIds = seeded.Collections["budgets"].Select(b => b["_id"].AsObjectId).ToHashSet();
        // A client whose name the dialogs can be searched by (no outer or double spaces).
        static bool Plain(BsonValue value) => value.IsString && value.AsString == value.AsString.Trim() && !value.AsString.Contains("  ", StringComparison.Ordinal);
        var client = seeded.Collections["entities"].Where(e => Plain(e["name"]) && e["type"] != "Fornecedor" && e.Contains("paymentCondition"))
            .Select(e => e["name"].AsString).Order(StringComparer.Ordinal).First();
        // The latest revision of the newest number that is complete and has no job (the menu offers "Nova revisão").
        var latest = seeded.Collections["budgets"].GroupBy(b => b["budgetNumber"].AsString)
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First())
            .OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal).Take(25).ToList();
        var revised = latest.First(b => b.GetValue("complete", false).AsBoolean && !b.GetValue("hasGenerateJob", false).AsBoolean);
        var revisedNumber = revised["budgetNumber"].AsString;
        var created = new BudgetValues(client, "Prensa E2E teste", "Eng Teste");
        try
        {
            var results = new List<(string App, IReadOnlyList<string> Messages, IReadOnlyList<string> Row, DbSnapshot Snapshot)>();
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
                var messages = new List<string> { await AfterAsync(budgets, () => budgets.CreateAsync(created)) };
                var number = NewNumber(await app.SnapshotAsync(Cancellation), seedIds);
                await budgets.CloneAsync(number, created with { Descriptive = "Copia E2E", PersonInCharge = "Sr Clone" });
                await budgets.CreateRevisionAsync(revisedNumber, revised.GetValue("revision", 0).ToDouble());
                await session.Page.ReloadAsync();
                await budgets.GotoAsync();
                results.Add((app.Name, messages, await budgets.RowAsync(number), await app.SnapshotAsync(Cancellation)));
            }

            var (legacy, ours) = (results[0], results[1]);
            ours.Messages.Should().Equal(legacy.Messages).And.Equal("Orçamento criado com sucesso!");
            ours.Row.Should().Equal(legacy.Row);
            ours.Row.Skip(1).Take(3).Should().Equal(client, "Prensa E2E teste", "Eng Teste");
            New(ours.Snapshot, seedIds).Should().Equal(New(legacy.Snapshot, seedIds));
            New(ours.Snapshot, seedIds).Should().HaveCount(3, "the new budget, its clone and the new revision");
            Tasks(ours.Snapshot, seedIds).Should().Equal(Tasks(legacy.Snapshot, seedIds));
            Without(ours.Snapshot, seedIds).DiffFrom(Without(legacy.Snapshot, seedIds)).Should().BeEmpty("every other document is the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation); // back to the seed for the other tests
        }
    }

    /// <summary>Runs <paramref name="action"/> and returns the next notification's text.</summary>
    private static async Task<string> AfterAsync(IBudgetsPage page, Func<Task> action)
    {
        var before = await page.Notification.CountAsync();
        await action();
        var next = page.Notification.Nth(before);
        await next.WaitForAsync(new() { Timeout = 15_000 });
        return (await next.InnerTextAsync()).Trim();
    }

    private static string NewNumber(DbSnapshot snapshot, HashSet<ObjectId> seed) =>
        snapshot.Collections["budgets"].Single(b => !seed.Contains(b["_id"].AsObjectId))["budgetNumber"].AsString;

    /// <summary>The new budgets without their ids and dates, in number/revision order.</summary>
    private static List<string> New(DbSnapshot snapshot, HashSet<ObjectId> seed) =>
    [
        .. snapshot.Collections["budgets"].Where(b => !seed.Contains(b["_id"].AsObjectId))
            .OrderBy(b => b["budgetNumber"].AsString, StringComparer.Ordinal).ThenBy(b => b["revision"].ToDouble())
            .Select(b =>
            {
                var copy = b.DeepClone().AsBsonDocument;
                copy["_id"] = "{{id}}";
                copy["date"] = "{{now}}";
                return copy.ToJson(Canonical);
            }),
    ];

    /// <summary>The tasks of the new budgets, by budget (number, revision) and in insertion order, without ids.</summary>
    private static List<string> Tasks(DbSnapshot snapshot, HashSet<ObjectId> seed)
    {
        var budgets = snapshot.Collections["budgets"].Where(b => !seed.Contains(b["_id"].AsObjectId))
            .ToDictionary(b => b["_id"].AsObjectId, b => $"{b["budgetNumber"].AsString} R{b["revision"].ToDouble()}");
        return
        [
            .. snapshot.Collections["tasks"].Where(t => budgets.ContainsKey(t["budgetId"].AsObjectId))
                .OrderBy(t => budgets[t["budgetId"].AsObjectId], StringComparer.Ordinal).ThenBy(t => t["_id"].AsObjectId)
                .Select(t =>
                {
                    var copy = t.DeepClone().AsBsonDocument;
                    copy["_id"] = "{{id}}";
                    copy["budgetId"] = budgets[t["budgetId"].AsObjectId];
                    return copy.ToJson(Canonical);
                }),
        ];
    }

    private static DbSnapshot Without(DbSnapshot snapshot, HashSet<ObjectId> seed)
    {
        var added = snapshot.Collections["budgets"].Where(b => !seed.Contains(b["_id"].AsObjectId)).Select(b => b["_id"].AsObjectId).ToHashSet();
        return DbSnapshot.From(snapshot.Collections.ToDictionary(
            c => c.Key,
            c => c.Value.Where(d => !(c.Key == "budgets" && added.Contains(d["_id"].AsObjectId))
                                    && !(c.Key == "tasks" && d.Contains("budgetId") && added.Contains(d["budgetId"].AsObjectId)))));
    }
}
