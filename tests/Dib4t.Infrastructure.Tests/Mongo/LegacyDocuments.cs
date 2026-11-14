using System.Globalization;
using System.Text.RegularExpressions;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>Legacy documents from the committed fixtures, and the read → write round trip through the class maps.</summary>
internal static partial class LegacyDocuments
{
    /// <summary>The anonymized production backup, per collection (30,754 documents).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>> Production { get; } = LoadProduction();

    /// <summary>
    /// A characterization DB dump (<c>00-initial-db.json</c>, <c>99-final-db.json</c>), documents written by the real
    /// legacy app. Placeholders: <c>{{budgets:1}}</c>-style ids become fixed ObjectIds, <c>{{now}}</c> a fixed date.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>> Characterization(string file)
    {
        var json = File.ReadAllText(Fixtures.Characterization(file));
        var ids = new Dictionary<string, string>();
        json = IdPlaceholder().Replace(json, match =>
        {
            if (!ids.TryGetValue(match.Groups[1].Value, out var id))
            {
                id = "61" + ids.Count.ToString("x22", CultureInfo.InvariantCulture);
                ids[match.Groups[1].Value] = id;
            }

            return $"\"$oid\": \"{id}\"";
        });
        json = NowPlaceholder().Replace(json, "\"$date\": \"2026-11-14T10:00:00.000Z\"");
        var dump = BsonDocument.Parse(json);
        return dump.Elements.ToDictionary(
            element => element.Name,
            element => (IReadOnlyList<BsonDocument>)[.. element.Value.AsBsonArray.Select(value => value.AsBsonDocument)]);
    }

    /// <summary>Reads <paramref name="original"/> into its document class and writes it back.</summary>
    public static BsonDocument RoundTrip(string collection, BsonDocument original)
    {
        MongoMappings.Register();
        var type = MongoCollectionNames.ByDocumentType.Single(pair => pair.Value == collection).Key;
        var document = BsonSerializer.Deserialize(original, type);
        return document.ToBsonDocument(type);
    }

    public static TDocument Read<TDocument>(BsonDocument original)
    {
        MongoMappings.Register();
        return BsonSerializer.Deserialize<TDocument>(original);
    }

    public static BsonDocument Write<TDocument>(TDocument document)
    {
        MongoMappings.Register();
        return document.ToBsonDocument();
    }

    private static Dictionary<string, IReadOnlyList<BsonDocument>> LoadProduction() =>
        DibbakFile.Read(Fixtures.ProductionBackup).Collections.ToDictionary(
            collection => collection.Name,
            collection => (IReadOnlyList<BsonDocument>)[.. collection.Documents.Select(raw => BsonSerializer.Deserialize<BsonDocument>(raw))]);

    [GeneratedRegex("\"\\$oid\":\\s*\"\\{\\{([a-z]+:\\d+)\\}\\}\"")]
    private static partial Regex IdPlaceholder();

    [GeneratedRegex("\"\\$date\":\\s*\"\\{\\{now\\}\\}\"")]
    private static partial Regex NowPlaceholder();
}

/// <summary>What changed between a legacy document and its round trip, by kind and path (field order ignored).</summary>
internal sealed record BsonChange(BsonChangeKind Kind, string Path, string Detail)
{
    public override string ToString() => $"{Kind} {Path} {Detail}";

    public static List<BsonChange> Between(BsonDocument original, BsonDocument written)
    {
        var changes = new List<BsonChange>();
        Compare(original, written, string.Empty, changes);
        return changes;
    }

    private static void Compare(BsonDocument original, BsonDocument written, string prefix, List<BsonChange> changes)
    {
        foreach (var element in original)
        {
            var path = prefix + element.Name;
            if (!written.TryGetValue(element.Name, out var value))
            {
                changes.Add(element.Value.IsBsonNull
                    ? new BsonChange(BsonChangeKind.DroppedNull, path, string.Empty)
                    : new BsonChange(BsonChangeKind.Lost, path, element.Value.ToJson()));
            }
            else
            {
                CompareValues(element.Value, value, path, changes);
            }
        }

        foreach (var element in written)
        {
            if (!original.Contains(element.Name))
            {
                changes.Add(new BsonChange(BsonChangeKind.AddedDefault, prefix + element.Name, element.Value.ToJson()));
            }
        }
    }

    private static void CompareValues(BsonValue original, BsonValue written, string path, List<BsonChange> changes)
    {
        if (original.IsBsonDocument && written.IsBsonDocument)
        {
            Compare(original.AsBsonDocument, written.AsBsonDocument, path + ".", changes);
        }
        else if (original.IsBsonArray && written.IsBsonArray && original.AsBsonArray.Count == written.AsBsonArray.Count)
        {
            for (var i = 0; i < original.AsBsonArray.Count; i++)
            {
                CompareValues(original.AsBsonArray[i], written.AsBsonArray[i], path + "[]", changes);
            }
        }
        else if (original.BsonType != written.BsonType && original.IsNumeric && written.IsNumeric
                 && original.ToDouble().Equals(written.ToDouble()))
        {
            changes.Add(new BsonChange(BsonChangeKind.Retyped, path, $"{original.BsonType} → {written.BsonType}"));
        }
        else if (original.BsonType != written.BsonType || !original.Equals(written))
        {
            changes.Add(new BsonChange(BsonChangeKind.Changed, path, $"{original.ToJson()} → {written.ToJson()}"));
        }
    }
}

internal enum BsonChangeKind
{
    /// <summary>Missing in the legacy document; written with the Mongoose default.</summary>
    AddedDefault,

    /// <summary>A stored <c>null</c> of a field the class maps leave out when null.</summary>
    DroppedNull,

    /// <summary>Same number, other BSON type (the legacy driver's rule).</summary>
    Retyped,

    /// <summary>Value or type changed: never allowed.</summary>
    Changed,

    /// <summary>A field the round trip did not write: never allowed.</summary>
    Lost,
}
