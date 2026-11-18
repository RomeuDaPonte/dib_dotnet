using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every case of <c>job-board-golden.json</c> (<see cref="JobGoldenReplay"/>). <c>PATCH state</c> follows the legacy
/// code with D1 fixed (<c>fixed</c>); its <c>legacy</c> answer is checked to be the D1 400 wherever the fix changes it.
/// </summary>
public sealed class JobBoardGoldenTests(AppWithGoldenClock app)
    : JobGoldenReplay(app, BoardGolden), IClassFixture<AppWithGoldenClock>
{
    private static readonly JsonElement BoardGolden = JsonElement.Parse(File.ReadAllText(Fixtures.JobBoardGolden));

    public static TheoryData<int> Cases => CaseIndexes(BoardGolden);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Each_legacy_case_gives_the_same_answer_and_documents(int index) => ReplayAsync(index);

    [Fact]
    public void D1_the_legacy_state_route_never_finds_the_job()
    {
        foreach (var item in Golden.GetProperty("cases").EnumerateArray().Where(c => c.TryGetProperty("fixed", out _)))
        {
            var legacy = item.GetProperty("legacy");
            var fixedOutcome = item.GetProperty("fixed");
            if (fixedOutcome.GetProperty("status").GetInt32() == 200)
            {
                (legacy.GetProperty("status").GetInt32(), legacy.GetProperty("text").GetString()).Should().Be((400, "Obra não existe"));
                legacy.GetProperty("effects").GetArrayLength().Should().Be(0);
            }
            else
            {
                legacy.GetRawText().Should().Be(fixedOutcome.GetRawText(), "the fix changes only the lookup");
            }
        }
    }
}
