using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.Tests.Common;

/// <summary>
/// All documents of a database, per collection, ordered by <c>_id</c>. Compared field by field <b>including the BSON
/// type</b> and the field order (PLAN.md §11.7: "the resulting DB documents, field by field and by BSON type"), so an
/// Int32 that became a Double, or a reordered document, is a difference.
/// </summary>
public sealed class DbSnapshot
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private DbSnapshot(IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>> collections) => Collections = collections;

    public IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>> Collections { get; }

    public int DocumentCount => Collections.Values.Sum(c => c.Count);

    public static DbSnapshot From(IDictionary<string, IEnumerable<BsonDocument>> collections) =>
        new(collections.ToDictionary(
            c => c.Key,
            c => (IReadOnlyList<BsonDocument>)[.. c.Value.OrderBy(d => IdKey(d), StringComparer.Ordinal)],
            StringComparer.Ordinal));

    /// <summary>Differences from <paramref name="other"/>, as readable lines; empty when both hold the same data.</summary>
    public IReadOnlyList<string> DiffFrom(DbSnapshot other, int maxDifferences = 50)
    {
        var diffs = new List<string>();
        foreach (var name in Collections.Keys.Union(other.Collections.Keys).Order(StringComparer.Ordinal))
        {
            var mine = Collections.GetValueOrDefault(name, []).ToDictionary(IdKey);
            var theirs = other.Collections.GetValueOrDefault(name, []).ToDictionary(IdKey);
            if (!Collections.ContainsKey(name) || !other.Collections.ContainsKey(name))
            {
                diffs.Add($"{name}: collection only on the {(Collections.ContainsKey(name) ? "left" : "right")} side");
                continue;
            }

            foreach (var id in mine.Keys.Union(theirs.Keys).Order(StringComparer.Ordinal))
            {
                if (diffs.Count >= maxDifferences)
                {
                    diffs.Add("… (more differences not shown)");
                    return diffs;
                }

                if (!mine.TryGetValue(id, out var left))
                {
                    diffs.Add($"{name} {id}: only on the right side");
                }
                else if (!theirs.TryGetValue(id, out var right))
                {
                    diffs.Add($"{name} {id}: only on the left side");
                }
                else
                {
                    CompareValues($"{name} {id}", string.Empty, left, right, diffs);
                }
            }
        }

        return diffs;
    }

    /// <summary>Compares two values at <paramref name="path"/> (dotted, <c>[i]</c> for arrays) of document <paramref name="document"/>.</summary>
    private static void CompareValues(string document, string path, BsonValue left, BsonValue right, List<string> diffs)
    {
        void Add(string what) => diffs.Add($"{document} {(path.Length == 0 ? "(document)" : path)}: {what}");

        if (left.BsonType != right.BsonType)
        {
            Add($"{Describe(left)} vs {Describe(right)}");
            return;
        }

        switch (left)
        {
            case BsonDocument leftDoc:
                var rightDoc = right.AsBsonDocument;
                var leftNames = leftDoc.Names.ToList();
                var rightNames = rightDoc.Names.ToList();
                foreach (var field in leftNames.Union(rightNames))
                {
                    var fieldPath = path.Length == 0 ? field : $"{path}.{field}";
                    if (!leftDoc.Contains(field) || !rightDoc.Contains(field))
                    {
                        diffs.Add($"{document} {fieldPath}: {(leftDoc.Contains(field) ? Describe(leftDoc[field]) : "missing")} vs {(rightDoc.Contains(field) ? Describe(rightDoc[field]) : "missing")}");
                    }
                    else
                    {
                        CompareValues(document, fieldPath, leftDoc[field], rightDoc[field], diffs);
                    }
                }

                var commonLeft = leftNames.Where(rightNames.Contains).ToList();
                var commonRight = rightNames.Where(leftNames.Contains).ToList();
                if (!commonLeft.SequenceEqual(commonRight))
                {
                    Add($"field order [{string.Join(',', commonLeft)}] vs [{string.Join(',', commonRight)}]");
                }

                break;
            case BsonArray leftArray:
                var rightArray = right.AsBsonArray;
                if (leftArray.Count != rightArray.Count)
                {
                    Add($"array length {leftArray.Count} vs {rightArray.Count}");
                    return;
                }

                for (var i = 0; i < leftArray.Count; i++)
                {
                    CompareValues(document, $"{path}[{i}]", leftArray[i], rightArray[i], diffs);
                }

                break;
            default:
                if (left.ToJson(Canonical) != right.ToJson(Canonical))
                {
                    Add($"{Describe(left)} vs {Describe(right)}");
                }

                break;
        }
    }

    private static string Describe(BsonValue value)
    {
        var json = value.ToJson(Canonical);
        return string.Create(CultureInfo.InvariantCulture, $"{value.BsonType} {(json.Length > 80 ? json[..80] + "…" : json)}");
    }

    private static string IdKey(BsonDocument document) =>
        document.TryGetValue("_id", out var id) ? id.ToJson(Canonical) : document.ToJson(Canonical);
}
