using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Dib4t.Infrastructure.Mongo.Serialization;

/// <summary>
/// Every number of the legacy model is a JavaScript number, C# <see cref="double"/> (PLAN.md §4.3 rule 1, §4.4 rule 1).
/// <para><b>Write</b> exactly like the legacy Node driver (js-bson 1.1.6 <c>serializeNumber</c>): an integral value in
/// the Int32 range is <c>Int32</c> (also <c>-0</c>, which becomes <c>0</c>); everything else (fractions, larger
/// integers, NaN, ±Infinity) is <c>Double</c>. Never <c>Int64</c> or <c>Decimal128</c>.</para>
/// <para><b>Read</b> <c>Int32</c>, <c>Int64</c> and <c>Double</c> (Int64 beyond 2^53 rounds, as in JavaScript);
/// <c>Decimal128</c> is converted too (never seen in legacy data, tolerated for older backups). Any other BSON type
/// is a <see cref="FormatException"/>.</para>
/// </summary>
public sealed class JsNumberSerializer : SerializerBase<double>
{
    public static JsNumberSerializer Instance { get; } = new();

    /// <summary>For <c>double?</c> members: BSON <c>null</c> ↔ <c>null</c>.</summary>
    public static IBsonSerializer<double?> Nullable { get; } = new NullableSerializer<double>(Instance);

    /// <summary>The BSON type the legacy driver writes for <paramref name="value"/>.</summary>
    public static BsonType BsonTypeFor(double value) => IsInt32(value) ? BsonType.Int32 : BsonType.Double;

    /// <summary>The value as the legacy driver writes it (for tests and raw <see cref="BsonDocument"/> updates).</summary>
    public static BsonValue ToBsonValue(double value) => IsInt32(value) ? new BsonInt32((int)value) : new BsonDouble(value);

    public override double Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reader = context.Reader;
        var type = reader.GetCurrentBsonType();
        return type switch
        {
            BsonType.Int32 => reader.ReadInt32(),
            BsonType.Int64 => reader.ReadInt64(),
            BsonType.Double => reader.ReadDouble(),
            BsonType.Decimal128 => (double)reader.ReadDecimal128(),
            _ => throw new FormatException(string.Create(
                CultureInfo.InvariantCulture, $"Cannot read a number from BSON type {type}.")),
        };
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, double value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsInt32(value))
        {
            context.Writer.WriteInt32((int)value);
        }
        else
        {
            context.Writer.WriteDouble(value);
        }
    }

    // Math.floor(v) === v && v in [-2^31, 2^31 - 1]: NaN and ±Infinity fail the range test; -0 passes (→ Int32 0).
    private static bool IsInt32(double value) =>
        value >= int.MinValue && value <= int.MaxValue && Math.Floor(value) == value;
}
