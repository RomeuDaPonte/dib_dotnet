using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Common;

/// <summary><c>tests/fixtures/legacy/settings/settings-golden.json</c>: what the legacy code answers (tools/legacy/settings-golden.js).</summary>
internal static class SettingsGolden
{
    public static JsonElement Root { get; } = JsonElement.Parse(File.ReadAllText(Fixtures.SettingsGolden));

    public static IEnumerable<JsonElement> Cases(string section) =>
        (Root.GetProperty(section).ValueKind == JsonValueKind.Object ? Root.GetProperty(section).GetProperty("cases") : Root.GetProperty(section))
        .EnumerateArray();

    /// <summary>The cases of <paramref name="section"/> as theory rows: the index (stable names) and the raw body.</summary>
    public static TheoryData<int, string> Rows(string section)
    {
        var data = new TheoryData<int, string>();
        var index = 0;
        foreach (var item in Cases(section))
        {
            data.Add(index++, item.GetProperty("body").GetString()!);
        }

        return data;
    }

    public static JsonElement Case(string section, int index) => Cases(section).ElementAt(index);

    public static string? Message(this JsonElement item) =>
        item.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;

    public static string? Outcome(this JsonElement item) => item.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : null;
}
