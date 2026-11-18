using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dib4t.SharedKernel;

/// <summary>
/// A stored value as a legacy Mongoose document holds it: <b>missing</b> (<c>undefined</c>: not in the document, not in
/// its JSON), <b>null</b> (stored <c>null</c>, JSON <c>null</c>) or a value. The legacy answers keep the difference
/// (e.g. <c>jobs.deliveryDate</c> is <c>null</c> in 126 production jobs and missing in 58; an unset
/// <c>invoicedPercent</c> leaves the answer), so the domain and the contracts keep it too. <c>default</c> is missing:
/// with <c>[JsonIgnore(Condition = WhenWritingDefault)]</c> a missing value is left out of the JSON.
/// </summary>
[JsonConverter(typeof(StoredJsonConverterFactory))]
public readonly record struct Stored<T>
{
    private readonly byte _state; // 0 missing, 1 null, 2 value

    private Stored(T? value, byte state)
    {
        Value = value;
        _state = state;
    }

#pragma warning disable CA1000 // static factory members on the generic type read best at the call site
    /// <summary>Not stored (<c>undefined</c>).</summary>
    public static Stored<T> Missing => default;

    /// <summary>Stored <c>null</c>.</summary>
    public static Stored<T> Null => new(default, 1);

    /// <summary>The value; <c>default</c> unless <see cref="HasValue"/>.</summary>
    public T? Value { get; }

    public bool IsMissing => _state == 0;

    public bool IsNull => _state == 1;

    public bool HasValue => _state == 2;

    /// <summary>A stored value (a <c>null</c> reference is stored <c>null</c>).</summary>
    public static Stored<T> Of(T? value) => value is null ? Null : new(value, 2);
#pragma warning restore CA1000

    /// <summary>The value, or <c>null</c> when missing or stored <c>null</c>.</summary>
    public T? OrDefault() => HasValue ? Value : default;

    public override string ToString() => IsMissing ? "undefined" : IsNull ? "null" : Value?.ToString() ?? "null";
}

/// <summary>JSON of <see cref="Stored{T}"/>: <c>null</c> ↔ <see cref="Stored{T}.Null"/>, a value ↔ the value.</summary>
public sealed class StoredJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert is { IsGenericType: true } && typeToConvert.GetGenericTypeDefinition() == typeof(Stored<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(typeof(StoredJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class StoredJsonConverter<T> : JsonConverter<Stored<T>>
    {
        public override bool HandleNull => true;

        public override Stored<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? Stored<T>.Null : Stored<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, Stored<T> value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            if (value.HasValue)
            {
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
