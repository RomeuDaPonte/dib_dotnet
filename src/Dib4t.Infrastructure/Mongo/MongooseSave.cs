using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// The update a legacy Mongoose <c>doc.set(…)</c> + <c>doc.save()</c> sends (PLAN.md §4.3 rules 3–4, checked against
/// <c>$__delta</c> of the legacy models in <c>settings-golden.json</c>): a path is written only when the assigned
/// value differs from the stored one or the field is not stored at all (a default Mongoose applied on read is not
/// stored); numbers compare as JavaScript numbers (<c>Int32 10</c> equals <c>10.0</c>, so the stored type is kept);
/// arrays element by element. Assigning <c>undefined</c> removes a stored field (<c>$unset</c>). Replacing an array
/// adds <c>$inc: { __v: 1 }</c> (also when <c>__v</c> is missing: <c>$inc</c> then creates it). The filter is the
/// <c>_id</c> only, like Mongoose without a versioned array operation.
/// </summary>
internal static class MongooseSave
{
    /// <summary>
    /// The update for <paramref name="assignments"/> (field → value; a <c>null</c> value = <c>undefined</c>), applied
    /// to <paramref name="stored"/> in memory too (so callers can answer the saved document); <c>null</c> when nothing
    /// differs (Mongoose then sends no command). A field the document lacks but whose schema has a static default
    /// (<paramref name="defaults"/>) holds that default after Mongoose's read, so assigning the default value writes
    /// nothing (checked in <c>budget-golden.json</c>: an imported budget without <c>complete</c> saved with
    /// <c>complete = false</c> sends no <c>$set</c>).
    /// </summary>
    /// <remarks>
    /// With <paramref name="keepEquivalentTexts"/>, a text that is the NFC form of the stored one (canonically
    /// equivalent: the same characters) counts as unchanged, so a save of an unchanged imported text keeps it as
    /// stored (PLAN.md §4.5 item 6: 6 non-NFC texts in production; NFC applies only to texts the app changes).
    /// </remarks>
    public static UpdateDefinition<BsonDocument>? Update(
        BsonDocument stored,
        IEnumerable<(string Field, BsonValue? Value)> assignments,
        IReadOnlyDictionary<string, BsonValue>? defaults = null,
        bool keepEquivalentTexts = false)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(assignments);
        var set = new BsonDocument();
        var unset = new BsonDocument();
        var replacesArray = false;
        foreach (var (field, value) in assignments)
        {
            if (value is null)
            {
                if (Remove(stored, field))
                {
                    unset[field] = 1;
                }

                continue;
            }

            if (TryGet(stored, field, out var current) && JsEquals(current, value))
            {
                continue;
            }

            if (keepEquivalentTexts && current is { IsString: true } && value.IsString
                && string.Equals(current.AsString.Normalize(System.Text.NormalizationForm.FormC), value.AsString, StringComparison.Ordinal))
            {
                continue;
            }

            if (current is null && defaults is not null && defaults.TryGetValue(field, out var fallback) && JsEquals(fallback, value))
            {
                continue;
            }

            set[field] = value;
            Set(stored, field, value);
            replacesArray |= value.IsBsonArray;
        }

        if (set.ElementCount == 0 && unset.ElementCount == 0)
        {
            return null;
        }

        var update = new BsonDocument();
        if (replacesArray)
        {
            update["$inc"] = new BsonDocument("__v", 1);
            stored["__v"] = stored.TryGetValue("__v", out var version) && version.IsNumeric ? version.ToInt32() + 1 : 1;
        }

        if (set.ElementCount > 0)
        {
            update["$set"] = set;
        }

        if (unset.ElementCount > 0)
        {
            update["$unset"] = unset;
        }

        return update;
    }

    /// <summary>The value at <paramref name="path"/> (<c>parent.child</c> for a field of a sub-document).</summary>
    public static bool TryGet(BsonDocument document, string path, out BsonValue value)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return document.TryGetValue(path, out value!);
        }

        value = BsonNull.Value;
        return document.TryGetValue(path[..dot], out var parent) && parent.IsBsonDocument && TryGet(parent.AsBsonDocument, path[(dot + 1)..], out value);
    }

    /// <summary>Sets <paramref name="path"/> (a new field is added at the end of its document, as MongoDB does).</summary>
    public static void Set(BsonDocument document, string path, BsonValue value)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            document[path] = value;
            return;
        }

        var name = path[..dot];
        if (!document.TryGetValue(name, out var parent) || !parent.IsBsonDocument)
        {
            parent = new BsonDocument();
            document[name] = parent;
        }

        Set(parent.AsBsonDocument, path[(dot + 1)..], value);
    }

    /// <summary>Removes <paramref name="path"/>; <c>false</c> when it is not stored.</summary>
    public static bool Remove(BsonDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return RemoveTop(document, path);
        }

        return document.TryGetValue(path[..dot], out var parent) && parent.IsBsonDocument && Remove(parent.AsBsonDocument, path[(dot + 1)..]);
    }

    private static bool RemoveTop(BsonDocument document, string name)
    {
        if (!document.Contains(name))
        {
            return false;
        }

        document.Remove(name);
        return true;
    }

    /// <summary>Equality of two stored values as JavaScript (Mongoose <c>deepEqual</c>) sees them.</summary>
    public static bool JsEquals(BsonValue left, BsonValue right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.IsNumeric && right.IsNumeric)
        {
            return left.ToDouble() == right.ToDouble();
        }

        if (left.IsBsonArray && right.IsBsonArray)
        {
            var a = left.AsBsonArray;
            var b = right.AsBsonArray;
            return a.Count == b.Count && a.Zip(b).All(pair => JsEquals(pair.First, pair.Second));
        }

        return left.Equals(right);
    }
}
