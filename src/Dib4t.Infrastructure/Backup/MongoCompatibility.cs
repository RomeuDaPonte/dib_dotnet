using Dib4t.Application.Database;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// Cleans the collection options and index specs of a backup for MongoDB 9.0 before the restore recreates them
/// (PLAN.md §7.6 "Import of legacy backups"): only what 9.0 knows and accepts for a plain collection is kept; the
/// rest is left out and reported as a <see cref="RestoreAdjustment"/>. Documents are never touched.
/// </summary>
/// <remarks>
/// Checked against <c>mongo:9.0.8</c> (2026-11-22; <c>MongoCompatibilityTests</c> keeps the evidence): <c>create</c>
/// refuses unknown fields (<c>temp</c>, <c>uuid</c>, <c>recordPreImages</c>, …), <c>size</c>/<c>max</c> without
/// <c>capped</c> and <c>expireAfterSeconds</c> without <c>clusteredIndex</c>, and silently ignores <c>flags</c> and
/// <c>idIndex</c>; <c>createIndexes</c> refuses unknown fields (<c>safe</c>, …), <c>v: 0</c>, a NaN TTL and
/// geoHaystack indexes ("cannot be created in version 5.0 and above"), and still takes <c>background</c> (ignored
/// since 4.2) and <c>dropDups</c> (removed in 3.0). <c>v</c> and <c>ns</c> are dropped earlier, silently, like
/// legacy (<see cref="DibbakReader"/>). On a collection with a default collation it refuses a text or geo index
/// without the simple collation it was built with (<c>listIndexes</c> does not show it): that one is added back.
/// What the server still refuses is left out during the restore and reported (<c>MongoDatabaseBackupService</c>).
/// </remarks>
internal static class MongoCompatibility
{
    /// <summary><c>create</c> options of a plain collection on MongoDB 9.0 (views and time series are never in a backup).</summary>
    private static readonly HashSet<string> CollectionOptions = new(StringComparer.Ordinal)
    {
        "capped", "size", "max", "storageEngine", "validator", "validationLevel", "validationAction",
        "indexOptionDefaults", "collation", "clusteredIndex", "expireAfterSeconds", "changeStreamPreAndPostImages",
    };

    /// <summary>Index spec fields MongoDB 9.0 creates an index with (<c>key</c>, <c>name</c> and the index options).</summary>
    private static readonly HashSet<string> IndexFields = new(StringComparer.Ordinal)
    {
        "key", "name", "unique", "sparse", "partialFilterExpression", "expireAfterSeconds", "hidden", "storageEngine",
        "weights", "default_language", "language_override", "textIndexVersion", "2dsphereIndexVersion", "bits", "min",
        "max", "collation", "wildcardProjection", "prepareUnique", "coarsestIndexedLevel", "finestIndexedLevel",
    };

    /// <summary>The options MongoDB 9.0 accepts; what was left out is added to <paramref name="adjustments"/>.</summary>
    public static BsonDocument CleanOptions(string collection, BsonDocument options, List<RestoreAdjustment> adjustments)
    {
        var kept = new BsonDocument();
        var removed = new List<string>();
        var capped = options.TryGetValue("capped", out var cappedValue) && cappedValue.ToBoolean();
        var clustered = options.Contains("clusteredIndex");
        foreach (var option in options)
        {
            var known = CollectionOptions.Contains(option.Name)
                && (option.Name is not ("size" or "max") || capped)
                && (option.Name != "expireAfterSeconds" || clustered);
            if (known)
            {
                kept.Add(option);
            }
            else
            {
                removed.Add(option.Name);
            }
        }

        if (removed.Count > 0)
        {
            adjustments.Add(new RestoreAdjustment(collection, null, removed));
        }

        return kept;
    }

    /// <summary>Index types without collation support: on a collection with a default collation they need the simple one.</summary>
    private static readonly HashSet<string> TypesWithoutCollation = new(StringComparer.Ordinal) { "text", "2d", "2dsphere" };

    /// <summary>
    /// The spec MongoDB 9.0 creates, or <see langword="null"/> when 9.0 can no longer create such an index at all;
    /// what was left out is added to <paramref name="adjustments"/>.
    /// </summary>
    /// <param name="collection">The collection's name.</param>
    /// <param name="spec">The index spec of the file (without <c>v</c> and <c>ns</c>).</param>
    /// <param name="adjustments">Gets what was left out.</param>
    /// <param name="collectionHasCollation">
    /// The collection has a default collation: a text or geo index then gets <c>collation: { locale: "simple" }</c>,
    /// which is what it was built with (<c>listIndexes</c> does not show a simple collation, and without it the server
    /// refuses the index: "Index type 'text' does not support collation"). Not an adjustment: the index is the same.
    /// </param>
    public static BsonDocument? CleanIndex(string collection, BsonDocument spec, List<RestoreAdjustment> adjustments, bool collectionHasCollation = false)
    {
        var name = spec.GetValue("name", BsonNull.Value) is { IsString: true } n ? n.AsString : null;
        if (spec.GetValue("key", BsonNull.Value) is BsonDocument key && key.Any(k => k.Value is BsonString { Value: "geoHaystack" }))
        {
            adjustments.Add(new RestoreAdjustment(collection, name, [], IndexRemoved: true));
            return null;
        }

        var kept = new BsonDocument();
        var removed = new List<string>();
        foreach (var field in spec)
        {
            var valid = IndexFields.Contains(field.Name)
                && (field.Name != "expireAfterSeconds" || field.Value.IsNumeric && !double.IsNaN(field.Value.ToDouble()));
            if (valid)
            {
                kept.Add(field);
            }
            else
            {
                removed.Add(field.Name);
            }
        }

        if (removed.Count > 0)
        {
            adjustments.Add(new RestoreAdjustment(collection, name, removed));
        }

        if (collectionHasCollation && !kept.Contains("collation") && kept.GetValue("key", BsonNull.Value) is BsonDocument keys
            && keys.Any(k => k.Value is BsonString type && TypesWithoutCollation.Contains(type.Value)))
        {
            kept["collation"] = new BsonDocument("locale", "simple");
        }

        return kept;
    }
}
