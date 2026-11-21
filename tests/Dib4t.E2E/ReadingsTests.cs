using System.Diagnostics;
using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>
/// Readings (PLAN.md §7.4, §10 Phase 5 step 2): in both UIs, on the same seed, a "scanner" (keystrokes into the focused
/// field, no click, Enter) reads a billed job, then a job in production, then a number without a job. Each read asks
/// the same path, shows the same state in the same color (red for "Faturada"/"Para faturação", green otherwise; nothing
/// for no job), clears the field, closes after about 3 s and leaves the focus in the field for the next scan. An Enter
/// on the empty field fails the same way in both ("Algo deu errado", the dialog stays until "Sair"). Nothing is written.
/// </summary>
public sealed class ReadingsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";
    private const string NoJobNumber = "99-9999";

    [Fact]
    public async Task Both_uis_read_the_same_states_in_the_same_colors_and_close_after_3_seconds()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        seeded.Collections["budgets"].Should().NotContain(b => b["budgetNumber"] == NoJobNumber);
        var billed = Number(seeded, j => j["state"] is BsonString { Value: "Faturada" or "Para faturação" });
        var inProduction = Number(seeded, j => j["state"] is BsonString { Value: "Maquinação" or "Montagem" or "Desenvolvimento" });
        string[] numbers = [billed, inProduction, NoJobNumber];

        var reads = new List<IReadOnlyList<Reading>>();
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            await LoginAsync(app, session.Page);
            IReadingsPage readings = app == Harness.Legacy ? new LegacyReadingsPage(session.Page) : new NewReadingsPage(session.Page);
            await readings.GotoAsync();
            var appReads = new List<Reading>();
            foreach (var number in numbers)
            {
                appReads.Add(await ScanAsync(readings, number));
            }

            reads.Add(appReads);
            (await app.SnapshotAsync(Cancellation)).DiffFrom(seeded).Should().BeEmpty(app.Name + " writes nothing");
        }

        var (legacy, ours) = (reads[0], reads[1]);
        legacy.Select(r => r.Path).Should().Equal(numbers.Select(n => "/api/job/getByBudgetNumber/" + n));
        legacy.Select(r => (r.State, r.Color)).Should().Equal(
            (ExpectedState(seeded, billed), "rgb(255, 0, 0)"), (ExpectedState(seeded, inProduction), "rgb(0, 128, 0)"), (string.Empty, "rgb(0, 128, 0)"));
        legacy[2].EmptyBody.Should().BeTrue("legacy's empty 200 for a number without a job");
        ours.Select(r => (r.Path, r.Status, r.EmptyBody, r.State, r.Color, r.FieldAfter, r.FocusedAfter))
            .Should().Equal(legacy.Select(r => (r.Path, r.Status, r.EmptyBody, r.State, r.Color, r.FieldAfter, r.FocusedAfter)));
        ours.Should().OnlyContain(r => r.FieldAfter.Length == 0 && r.FocusedAfter, "cleared and focused for the next scan");
        foreach (var read in legacy.Concat(ours))
        {
            read.OpenFor.TotalSeconds.Should().BeInRange(2.5, 5.5, "closed about 3 s after the answer");
        }
    }

    [Fact]
    public async Task An_enter_on_the_empty_field_fails_the_same_way_in_both_uis()
    {
        var answers = new List<(string Path, int Status)>();
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            await LoginAsync(app, session.Page);
            IReadingsPage readings = app == Harness.Legacy ? new LegacyReadingsPage(session.Page) : new NewReadingsPage(session.Page);
            await readings.GotoAsync();

            var response = await session.Page.RunAndWaitForResponseAsync(() => session.Page.Keyboard.PressAsync("Enter"),
                r => r.Url.Contains("/api/job/", StringComparison.OrdinalIgnoreCase));
            answers.Add((new Uri(response.Url).AbsolutePath, response.Status));
            await session.Page.GetByText("Algo deu errado", new() { Exact = true }).First.WaitForAsync();
            await session.Page.WaitForTimeoutAsync(4_000);
            (await readings.State.CountAsync()).Should().Be(1, app.Name + ": the dialog stays open after a failure");
            await session.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Sair" }).ClickAsync(); // not the menu's logout
            await readings.State.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });
        }

        answers[0].Path.Should().Be("/api/job/getByBudgetNumber/");
        answers[1].Should().Be(answers[0]);
    }

    private async Task LoginAsync(IAppUnderTest app, IPage page)
    {
        if (app == Harness.Legacy)
        {
            await new LegacyLoginPage(page).GotoAsync();
            await new LegacyLoginPage(page).LoginAsync(FixtureUser, FixturePassword);
        }
        else
        {
            await new NewLoginPage(page).GotoAsync();
            await new NewLoginPage(page).LoginAsync(FixtureUser, FixturePassword);
        }
    }

    /// <summary>A scan: keystrokes into whatever has the focus (no click), then Enter; the dialog until it closes.</summary>
    private static async Task<Reading> ScanAsync(IReadingsPage readings, string number)
    {
        var page = readings.Page;
        var response = await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Keyboard.TypeAsync(number);
            await page.Keyboard.PressAsync("Enter");
        }, r => r.Url.Contains("/api/job/getByBudgetNumber/", StringComparison.OrdinalIgnoreCase));
        var answered = Stopwatch.StartNew();
        var body = await response.TextAsync();
        await readings.State.WaitForAsync(new() { State = WaitForSelectorState.Attached }); // an empty state has no size
        await page.WaitForTimeoutAsync(500); // the state is rendered from the answer
        var state = (await readings.State.TextContentAsync() ?? string.Empty).Trim();
        var color = await readings.State.EvaluateAsync<string>("e => getComputedStyle(e).color");
        await readings.State.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 8_000 });
        var openFor = answered.Elapsed;
        await page.WaitForTimeoutAsync(500); // focus restore after the close transition
        return new Reading(new Uri(response.Url).AbsolutePath, response.Status, body.Length == 0, state, color, openFor,
            await readings.Field.InputValueAsync(), await readings.Field.EvaluateAsync<bool>("e => e === document.activeElement"));
    }

    /// <summary>The number of the first seeded job (natural order) matching <paramref name="match"/>.</summary>
    private static string Number(DbSnapshot seed, Func<BsonDocument, bool> match)
    {
        var budgets = seed.Collections["budgets"].ToDictionary(b => b["_id"]);
        return seed.Collections["jobs"].Where(match)
            .Select(j => budgets.TryGetValue(j.GetValue("budget", BsonNull.Value), out var b) ? b["budgetNumber"].AsString : null)
            .First(n => n is not null)!;
    }

    /// <summary>Legacy <c>getByBudgetNumber</c>: the first job (natural order) whose budget has the number.</summary>
    private static string ExpectedState(DbSnapshot seed, string number)
    {
        var budgets = seed.Collections["budgets"].ToDictionary(b => b["_id"]);
        return seed.Collections["jobs"].First(j => budgets.TryGetValue(j.GetValue("budget", BsonNull.Value), out var b)
            && b["budgetNumber"] == number)["state"].AsString;
    }

    private sealed record Reading(string Path, int Status, bool EmptyBody, string State, string Color, TimeSpan OpenFor, string FieldAfter, bool FocusedAfter);
}
