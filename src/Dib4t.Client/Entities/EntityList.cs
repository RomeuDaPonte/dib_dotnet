using Dib4t.Contracts.Entities;

namespace Dib4t.Client.Entities;

/// <summary>
/// The entities table's client-side rules (legacy <c>entities/entitiesTable.jsx</c>, PLAN.md §7.2): the filter and
/// the sort keys.
/// </summary>
public static class EntityList
{
    /// <summary>
    /// Legacy <c>filterEntities</c>: the name contains the text in any case, or the location or the type contains it
    /// exactly (case-sensitive). An empty text shows everything. A missing name or location never matches (legacy:
    /// a TypeError that breaks the page; production has none).
    /// </summary>
    public static IEnumerable<EntityResponse> Filter(IEnumerable<EntityResponse> entities, string? text)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (string.IsNullOrEmpty(text))
        {
            return entities;
        }

        var lower = text.ToLowerInvariant();
        return entities.Where(e =>
            (e.Name?.ToLowerInvariant().Contains(lower, StringComparison.Ordinal) ?? false)
            || (e.Location?.Contains(text, StringComparison.Ordinal) ?? false)
            || (e.Type?.Contains(text, StringComparison.Ordinal) ?? false));
    }

    /// <summary>
    /// A sort key that orders like the legacy table (<c>stableSort</c> with JavaScript's <c>&lt;</c> on strings):
    /// UTF-16 code unit order, so upper case before lower case and accented letters after <c>z</c>. A missing value
    /// sorts as <c>""</c>.
    /// </summary>
    public static object SortKey(string? value) => new OrdinalText(value ?? string.Empty);

    private sealed record OrdinalText(string Value) : IComparable
    {
        public int CompareTo(object? obj) => obj is OrdinalText other ? string.CompareOrdinal(Value, other.Value) : 1;
    }
}
