using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every case of <c>job-details-golden.json</c> (<see cref="JobGoldenReplay"/>): <c>PATCH statisticsDates</c>,
/// <c>register</c>, <c>singleProperty</c> and <c>updateVerifications</c>. Sections with a decided fix are checked against
/// the legacy code with the fix (<c>fixed</c>: D17, D18, the verifier's name from the signed-in user), and the fixes are
/// checked to change only what they decide.
/// </summary>
public sealed class JobDetailsGoldenTests(AppWithGoldenClock app)
    : JobGoldenReplay(app, DetailsGolden), IClassFixture<AppWithGoldenClock>
{
    private readonly AppWithGoldenClock _app = app;

    private static readonly JsonElement DetailsGolden = JsonElement.Parse(File.ReadAllText(Fixtures.JobDetailsGolden));

    /// <summary>
    /// The known difference (as for the due date of job create): a date beyond the year 9999, which legacy stores
    /// (<c>+010000-01-01</c>), is a 500 with nothing written (.NET dates end in 9999; no such date in production).
    /// </summary>
    private static readonly JsonElement BeyondYear9999 = JsonElement.Parse("""{ "status": 500, "error": "known difference: beyond the year 9999", "effects": [] }""");

    public static TheoryData<int> Cases => CaseIndexes(DetailsGolden);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Each_legacy_case_gives_the_same_answer_and_documents(int index)
    {
        var item = DetailsGolden.GetProperty("cases")[index];
        return ReplayAsync(index, ExpectedOutcome(item).GetRawText().Contains("\"+010000-", StringComparison.Ordinal) ? BeyondYear9999 : null);
    }

    /// <summary>DoD 6 (§3.3): each route's use-case event is in the log store, under its request, with the user.</summary>
    [Fact]
    public async Task Each_detail_route_logs_its_event_with_the_request_and_the_user()
    {
        string[] sections = ["statisticsDates", "register", "singleProperty", "verifications"];
        string[] expected = ["devEndedInDate now", "registered value of", "changed", "verifications saved by"];
        var first = sections.Select(s => DetailsGolden.GetProperty("cases").EnumerateArray()
            .Select((c, i) => (c, i)).First(x => x.c.GetProperty("section").GetString() == s && ExpectedOutcome(x.c).GetProperty("effects").GetArrayLength() > 0).i).ToList();
        foreach (var index in first)
        {
            await ReplayAsync(index);
        }

        var rows = await Dib4t.Tests.Common.Logging.LogStoreFile.WaitForAsync(_app.LogStorePath,
            r => expected.All(e => r.Any(x => x.Message.StartsWith("Job ", StringComparison.Ordinal) && x.Message.Contains(e, StringComparison.Ordinal))),
            cancellationToken: Cancellation);
        foreach (var text in expected)
        {
            var row = rows.First(x => x.Message.StartsWith("Job ", StringComparison.Ordinal) && x.Message.Contains(text, StringComparison.Ordinal));
            (row.Level, row.Category, row.UserName).Should().Be(("Information", Dib4t.SharedKernel.LogCategories.App, TestUser.Admin.Name), text);
            row.RequestId.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Only_the_year_10000_case_is_a_known_difference() =>
        DetailsGolden.GetProperty("cases").EnumerateArray().Count(c => ExpectedOutcome(c).GetRawText().Contains("\"+010000-", StringComparison.Ordinal))
            .Should().Be(1);

    [Fact]
    public void D17_only_jobs_without_state_dates_change()
    {
        foreach (var (legacy, fixedOutcome, _) in Changed("statisticsDates"))
        {
            legacy.GetProperty("error").GetString().Should().StartWith("TypeError: Cannot set properties of undefined");
            fixedOutcome.GetProperty("effects").EnumerateArray().Should().AllSatisfy(e =>
                e.GetProperty("update").GetProperty("$set").EnumerateObject().Select(p => p.Name).Should().Equal("jobStatisticsDates"));
        }
    }

    [Fact]
    public void D18_only_consultancy_changes()
    {
        var changed = Changed("register").ToList();
        changed.Should().NotBeEmpty();
        changed.Should().AllSatisfy(c => c.Body.Should().Contain("\"taskType\":\"consultancy\""));
    }

    /// <summary>
    /// The verifier fix: the replaced statistic always carries the signed-in user's name. Where legacy writes, the fix
    /// writes the same sub-document but for <c>validatedBy</c>; where legacy writes nothing (the sent statistic equals
    /// the stored one, without a name), the fix writes the statistic with the name.
    /// </summary>
    [Fact]
    public void The_verifier_fix_changes_only_validatedBy()
    {
        var changed = Changed("verifications").ToList();
        changed.Should().NotBeEmpty();
        foreach (var (legacy, fixedOutcome, _) in changed)
        {
            fixedOutcome.GetProperty("effects").GetRawText().Should().Contain("\"validatedBy\": \"Ana Administradora\"");
            if (legacy.GetProperty("effects").GetArrayLength() > 0)
            {
                WithoutVerifier(fixedOutcome).Should().Be(WithoutVerifier(legacy));
            }
        }

        static string WithoutVerifier(JsonElement outcome) => System.Text.RegularExpressions.Regex.Replace(
            outcome.GetProperty("effects").GetRawText(), "(,\\s*)?\"validatedBy\":\\s*\"[^\"]*\"", string.Empty);
    }

    private static IEnumerable<(JsonElement Legacy, JsonElement Fixed, string Body)> Changed(string section) =>
        DetailsGolden.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("section").GetString() == section && c.TryGetProperty("fixed", out _))
            .Where(c => c.GetProperty("fixed").GetRawText() != c.GetProperty("legacy").GetRawText())
            .Select(c => (c.GetProperty("legacy"), c.GetProperty("fixed"), c.GetProperty("body").GetString() ?? string.Empty));
}
