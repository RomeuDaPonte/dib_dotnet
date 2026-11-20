using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every case of <c>job-dashboard-golden.json</c> (<see cref="JobGoldenReplay"/>): <c>GET
/// getValidTryOnFirstTestCountersByYear/:year</c> over HTTP on mongo:9.0.8 with the golden file's 24 jobs gives the
/// <c>fixed</c> answer (the legacy code with D10 fixed: the whole year in Europe/Lisbon) and writes nothing; D10 is
/// checked to change only the jobs at the ends of a year.
/// </summary>
public sealed class JobDashboardGoldenTests(AppWithGoldenClock app)
    : JobGoldenReplay(app, DashboardGolden), IClassFixture<AppWithGoldenClock>
{
    private static readonly JsonElement DashboardGolden = JsonElement.Parse(File.ReadAllText(Fixtures.JobDashboardGolden));

    /// <summary>
    /// The known difference (<see cref="Dib4t.Application.Jobs.DashboardYear"/>): texts only V8's fallback date parser
    /// reads, for which legacy counts another year (2001 for <c>abc</c>), are the 500 of an Invalid Date.
    /// </summary>
    private static readonly HashSet<string> FallbackYears = new(StringComparer.Ordinal) { "abc", "0", " 2026", "10000", "-000000" };

    private static readonly JsonElement InvalidDate = JsonElement.Parse("""{ "status": 500, "error": "known difference: V8's fallback date parser is not ported", "effects": [] }""");

    public static TheoryData<int> Cases => CaseIndexes(DashboardGolden);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Each_legacy_case_gives_the_same_answer_and_documents(int index) =>
        ReplayAsync(index, FallbackYears.Contains(Year(DashboardGolden.GetProperty("cases")[index])) ? InvalidDate : null);

    [Fact]
    public void The_known_differences_are_only_the_fallback_parser_texts()
    {
        var cases = DashboardGolden.GetProperty("cases").EnumerateArray().ToList();
        cases.Select(Year).Should().Contain(FallbackYears);
        foreach (var item in cases.Where(c => FallbackYears.Contains(Year(c))))
        {
            item.GetProperty("legacy").GetProperty("status").GetInt32().Should().Be(200, "legacy reads {0} with V8's fallback parser", Year(item));
        }
    }

    [Fact]
    public void D10_changes_only_the_jobs_at_the_ends_of_a_year()
    {
        var changed = DashboardGolden.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("legacy").TryGetProperty("json", out var legacy) && legacy.GetRawText() != c.GetProperty("fixed").GetProperty("json").GetRawText())
            .ToDictionary(Year, c => (Legacy: Counters(c.GetProperty("legacy")), Fixed: Counters(c.GetProperty("fixed"))), StringComparer.Ordinal);

        // 2026: 31 December at 12:00 ("1") and 23:59:59.999 ("2") (31 December 00:00 is in both); 2025: its last millisecond;
        // 1995 (Lisbon on UTC+1): 1994-12-31T23:30Z ("2") and 1995-12-31T22:00Z ("3"); 1996: 1995-12-31T23:30Z ("3").
        changed.Should().BeEquivalentTo(new Dictionary<string, ((int, int, int) Legacy, (int, int, int) Fixed)>(StringComparer.Ordinal)
        {
            ["2026"] = ((1, 2, 2), (2, 3, 2)),
            ["+002026"] = ((1, 2, 2), (2, 3, 2)),
            [" 2026"] = ((1, 2, 2), (2, 3, 2)),
            ["2025"] = ((0, 0, 0), (1, 0, 0)),
            ["1995"] = ((1, 0, 0), (1, 1, 1)),
            ["1996"] = ((0, 0, 0), (0, 0, 1)),
        });
    }

    private static string Year(JsonElement item) => item.GetProperty("year").GetString()!;

    private static (int, int, int) Counters(JsonElement outcome)
    {
        var json = outcome.GetProperty("json");
        return (json.GetProperty("oneCounter").GetInt32(), json.GetProperty("twoCounter").GetInt32(), json.GetProperty("threeCounter").GetInt32());
    }
}
