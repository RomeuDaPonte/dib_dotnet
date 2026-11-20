using System.Text.Json;
using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>
/// Dashboard (PLAN.md §7.3, §10 Phase 5 step 1): in both UIs, on the same seed, the page asks the counters of the
/// current year with the same request, gets the same answer, which is the seed's own count for the year in
/// Europe/Lisbon (D10: no seed job starts in the hours it moves), and shows the same legend: "3º teste", "2º teste",
/// "1º teste" in that order, without a slice of 0 (as Google Charts). Nothing is written.
/// </summary>
public sealed class DashboardTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";

    private static readonly string[] Slices = ["3º teste", "2º teste", "1º teste"];

    [Fact]
    public async Task Both_uis_ask_and_show_the_same_counters_of_the_current_year()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        var loads = new List<DashboardLoad>();
        var texts = new List<IReadOnlyList<string>>();
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            IDashboardPage dashboard = app == Harness.Legacy ? new LegacyDashboardPage(session.Page) : new NewDashboardPage(session.Page);
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

            loads.Add(await dashboard.GotoAsync());
            texts.Add(await dashboard.TextsAsync());
            (await app.SnapshotAsync(Cancellation)).DiffFrom(seeded).Should().BeEmpty(app.Name + " writes nothing");
        }

        var (legacy, ours) = (loads[0], loads[1]);
        var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Year;
        legacy.Path.Should().Be($"/api/job/getValidTryOnFirstTestCountersByYear/{year}", "the browser's year");
        (ours.Path, ours.Status, ours.Json).Should().Be((legacy.Path, legacy.Status, legacy.Json));

        var lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var started = seeded.Collections["jobs"]
            .Where(j => j.GetValue("state", BsonNull.Value) is BsonString { Value: "Para faturação" or "Faturada" } && j.GetValue("startDate", BsonNull.Value).IsValidDateTime)
            .Where(j => TimeZoneInfo.ConvertTimeFromUtc(j["startDate"].ToUniversalTime(), lisbon).Year == year)
            .Select(j => j.GetValue("assemblyStatistic", new BsonDocument()).AsBsonDocument.GetValue("validTryOnFirstTest", BsonNull.Value))
            .ToList();
        var answer = JsonElement.Parse(ours.Json);
        (answer.GetProperty("oneCounter").GetInt32(), answer.GetProperty("twoCounter").GetInt32(), answer.GetProperty("threeCounter").GetInt32())
            .Should().Be((started.Count(v => v == "1"), started.Count(v => v == "2"), started.Count(v => v == "3")), "the seed's jobs of {0}", year);

        // Both legends leave out a slice of 0 (legacy: Google Charts' legend; its slice labels follow the legend's texts).
        texts[0].Distinct().Should().BeSubsetOf(Slices);
        texts[1].Should().Equal(texts[0].Distinct(), "the same legend entries, in the legacy data order");
        texts[1].Should().Equal(Slices.Where((_, i) => i switch
        {
            0 => answer.GetProperty("threeCounter").GetInt32() > 0,
            1 => answer.GetProperty("twoCounter").GetInt32() > 0,
            _ => answer.GetProperty("oneCounter").GetInt32() > 0,
        }));
    }

    /// <summary>A year without counted jobs: legacy's Google pie of only zeros draws nothing (no slice, no legend, no text); nor does the new page.</summary>
    [Fact]
    public async Task Only_zeros_draw_nothing_in_both_uis()
    {
        foreach (var app in Harness.Apps)
        {
            var session = await OpenAsync(app);
            await session.Page.RouteAsync("**/api/job/getValidTryOnFirstTestCountersByYear/**", route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json; charset=utf-8",
                Body = """{"oneCounter":0,"twoCounter":0,"threeCounter":0}""",
            }));
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

            IDashboardPage dashboard = app == Harness.Legacy ? new LegacyDashboardPage(session.Page) : new NewDashboardPage(session.Page);
            (await dashboard.GotoEmptyAsync()).Should().BeEmpty(app.Name + " draws no text");
        }
    }
}
