namespace Dib4t.Application.Jobs;

/// <summary>
/// One assignment of the legacy job model before its <c>save()</c> (<c>this.comments = …</c>,
/// <c>this.jobStatisticsDates.devEndedInDate = new Date()</c>), in the order the legacy code makes them. The
/// repository turns them into Mongoose's update (a path is written only when the value changes; PLAN.md §4.3).
/// <see cref="Path"/> is an element name, or <c>parent.child</c> for a field of an existing sub-document.
/// </summary>
public readonly record struct JobAssignment(string Path, StoredValue Value);

/// <summary>A value as the legacy model stores it after Mongoose's cast (JavaScript types, BSON on save).</summary>
public abstract record StoredValue
{
    private StoredValue()
    {
    }

    /// <summary><c>undefined</c>: removes a stored field (<c>$unset</c>), writes nothing when it is missing.</summary>
    public static StoredValue Undefined { get; } = new UndefinedValue();

    /// <summary>Stored <c>null</c>.</summary>
    public static StoredValue Null { get; } = new NullValue();

    /// <summary>A new <c>ObjectId</c> (the <c>_id</c> of a new sub-document).</summary>
    public static StoredValue NewId { get; } = new NewIdValue();

    public static StoredValue Number(double value) => new NumberValue(value);

    public static StoredValue Text(string value) => new TextValue(value);

    public static StoredValue Boolean(bool value) => new BooleanValue(value);

    public static StoredValue Date(DateTime utc) => new DateValue(utc);

    /// <summary>A <c>[String]</c> array (a <c>null</c> element is stored <c>null</c>).</summary>
    public static StoredValue Texts(IReadOnlyList<string?> values) => new TextsValue(values);

    /// <summary>A sub-document with its fields in order.</summary>
    public static StoredValue Document(IReadOnlyList<KeyValuePair<string, StoredValue>> fields) => new DocumentValue(fields);

    public sealed record UndefinedValue : StoredValue;

    public sealed record NullValue : StoredValue;

    public sealed record NewIdValue : StoredValue;

    public sealed record NumberValue(double Value) : StoredValue;

    public sealed record TextValue(string Value) : StoredValue;

    public sealed record BooleanValue(bool Value) : StoredValue;

    public sealed record DateValue(DateTime Value) : StoredValue;

    public sealed record TextsValue(IReadOnlyList<string?> Values) : StoredValue;

    public sealed record DocumentValue(IReadOnlyList<KeyValuePair<string, StoredValue>> Fields) : StoredValue;
}
