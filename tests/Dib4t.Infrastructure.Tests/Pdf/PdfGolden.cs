using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Infrastructure.Pdf.Layout;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Dib4t.Infrastructure.Tests.Pdf;

/// <summary>Reads the PDF golden files (tests/fixtures/legacy/pdf-layout) and builds the same canonical page text as the generator.</summary>
internal static class PdfGolden
{
    private static readonly Lazy<JsonElement> LayoutsFile = new(() => Load("layouts.json"));

    public static JsonElement Load(string file) => JsonElement.Parse(File.ReadAllText(Fixtures.PdfLayout(file)));

    public static IEnumerable<JsonElement> Layouts => LayoutsFile.Value.GetProperty("cases").EnumerateArray();

    public static JsonElement Layout(string id) => Layouts.Single(c => c.GetProperty("id").GetString() == id);

    /// <summary>The printed inputs of a golden case (budgetPdfGenerator.js reads), with the legacy (UTC) document date.</summary>
    public static BudgetPdfContent Content(JsonElement inputs, TimeZoneInfo? zone = null)
    {
        var date = DateTime.Parse(inputs.GetProperty("date").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var client = inputs.GetProperty("client");
        var madeBy = inputs.GetProperty("madeBy");
        return new BudgetPdfContent(
            Text(inputs, "budgetNumber"),
            Number(inputs, "revision") ?? 0,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(date, zone ?? TimeZoneInfo.Utc)),
            Text(madeBy, "name"),
            Text(madeBy, "email"),
            Text(client, "name"),
            Text(client, "address"),
            Text(client, "location"),
            Text(client, "zipCode"),
            Text(client, "fiscalNumber"),
            Text(inputs, "personInCharge"),
            Text(inputs, "descriptive"),
            Text(inputs, "description"),
            Number(inputs, "daysNeededToDoTheWork") ?? double.NaN,
            Number(inputs, "validity") ?? double.NaN,
            Text(inputs, "paymentMethod"),
            Number(inputs, "total"));
    }

    /// <summary>
    /// The budgets of the anonymized backup as the legacy route prints them: Mongoose defaults for missing fields, the
    /// client and author populated, the UTC date.
    /// </summary>
    public static IEnumerable<(string Id, BudgetPdfContent Content)> FixtureBudgets()
    {
        var backup = DibbakFile.Read(Fixtures.ProductionBackup);
        IEnumerable<BsonDocument> Docs(string name) =>
            backup.Collections.Single(c => c.Name == name).Documents.Select(d => BsonSerializer.Deserialize<BsonDocument>(d));
        var entities = Docs("entities").ToDictionary(e => e["_id"].AsObjectId);
        var users = Docs("users").ToDictionary(u => u["_id"].AsObjectId);
        foreach (var b in Docs("budgets"))
        {
            var client = entities[b["client"].AsObjectId];
            var user = users[b["madeBy"].AsObjectId];
            yield return (b["_id"].AsObjectId.ToString(), new BudgetPdfContent(
                Text(b, "budgetNumber"),
                Number(b, "revision") ?? 0,
                DateOnly.FromDateTime(b["date"].ToUniversalTime()),
                Text(user, "name"),
                Text(user, "email"),
                Text(client, "name"),
                Text(client, "address"),
                Text(client, "location"),
                Text(client, "zipCode"),
                Text(client, "fiscalNumber"),
                Text(b, "personInCharge"),
                Text(b, "descriptive"),
                Text(b, "description"),
                Number(b, "daysNeededToDoTheWork") ?? 60,
                Number(b, "validity") ?? 30,
                Text(b, "paymentMethod"),
                Number(b, "total")));
        }
    }

    /// <summary>The canonical page text of tools/legacy/pdf-golden.js (numbers with toFixed(6), texts as UTF-16 hex).</summary>
    public static string Canonical(IReadOnlyList<Page> pages)
    {
        var text = new StringBuilder();
        for (var p = 0; p < pages.Count; p++)
        {
            text.Append("P ").Append(p.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var item in pages[p].Items)
            {
                switch (item)
                {
                    case LineItem { Line: var line }:
                        text.Append(CultureInfo.InvariantCulture, $"L {N(line.X)} {N(line.Y)} {N(line.Height)}\n");
                        foreach (var i in line.Inlines)
                        {
                            text.Append(CultureInfo.InvariantCulture, $"I {N(i.X)} {N(i.Width)} {i.Font.Name} {N(i.FontSize)} {Hex(i.Text)}\n");
                        }

                        break;
                    case VectorItem { Vector: LineVector l }:
                        text.Append(CultureInfo.InvariantCulture, $"V line {N(l.X1)} {N(l.Y1)} {N(l.X2)} {N(l.Y2)} {N(l.LineWidth)} {l.LineColor}\n");
                        break;
                    case VectorItem { Vector: RectVector r }:
                        text.Append(CultureInfo.InvariantCulture, $"V rect {N(r.X)} {N(r.Y)} {N(r.W)} {N(r.H)} {r.Color}\n");
                        break;
                    case ImageItem m:
                        text.Append(CultureInfo.InvariantCulture, $"M {N(m.X)} {N(m.Y)} {N(m.Width)} {N(m.Height)}\n");
                        break;
                    default:
                        break;
                }
            }
        }

        return text.ToString();
    }

    /// <summary>The canonical text of a golden case's dumped pages (to show the first difference).</summary>
    public static string Canonical(JsonElement pages)
    {
        var text = new StringBuilder();
        var p = 0;
        foreach (var page in pages.EnumerateArray())
        {
            text.Append(CultureInfo.InvariantCulture, $"P {p++}\n");
            foreach (var it in page.GetProperty("items").EnumerateArray())
            {
                switch (it.GetProperty("type").GetString())
                {
                    case "line":
                        text.Append(CultureInfo.InvariantCulture, $"L {N(it, "x")} {N(it, "y")} {N(it, "height")}\n");
                        foreach (var i in it.GetProperty("inlines").EnumerateArray())
                        {
                            text.Append(CultureInfo.InvariantCulture,
                                $"I {N(i, "x")} {N(i, "width")} {i.GetProperty("font").GetString()} {N(i, "size")} {Hex(i.GetProperty("text").GetString()!)}\n");
                        }

                        break;
                    case "vector" when it.GetProperty("shape").GetString() == "line":
                        text.Append(CultureInfo.InvariantCulture,
                            $"V line {N(it, "x1")} {N(it, "y1")} {N(it, "x2")} {N(it, "y2")} {N(it, "lineWidth")} {it.GetProperty("lineColor").GetString()}\n");
                        break;
                    case "vector":
                        text.Append(CultureInfo.InvariantCulture,
                            $"V {it.GetProperty("shape").GetString()} {N(it, "x")} {N(it, "y")} {N(it, "w")} {N(it, "h")} {it.GetProperty("color").GetString()}\n");
                        break;
                    default:
                        text.Append(CultureInfo.InvariantCulture, $"M {N(it, "x")} {N(it, "y")} {N(it, "w")} {N(it, "h")}\n");
                        break;
                }
            }
        }

        return text.ToString();
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The first differing line of two canonical texts, for a readable failure.</summary>
    public static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end>";
            var al = i < a.Length ? a[i] : "<end>";
            if (el != al)
            {
                return $"line {i + 1}: expected «{Decode(el)}» but was «{Decode(al)}»";
            }
        }

        return "no difference";
    }

    private static string Decode(string canonicalLine)
    {
        var parts = canonicalLine.Split(' ');
        if (parts is ["I", .., var hex] && hex.Length % 4 == 0 && hex.Length > 0)
        {
            var chars = Enumerable.Range(0, hex.Length / 4).Select(k => (char)int.Parse(hex.AsSpan(k * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return canonicalLine + " (" + new string([.. chars]) + ")";
        }

        return canonicalLine;
    }

    private static string N(double v) => JsNumber.ToFixed(v, 6);

    private static string N(JsonElement e, string name) => N(e.GetProperty(name).GetDouble());

    private static string Hex(string s) => string.Concat(s.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture)));

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Text(BsonDocument d, string name) => d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null;

    private static double? Number(BsonDocument d, string name) => d.TryGetValue(name, out var v) && v.IsNumeric ? v.ToDouble() : null;
}
