using System.Text.Json;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// <c>JSON.stringify</c> of a BSON value as the legacy back-end has it after the MongoDB driver 3.7 read it (js-bson
/// 1.x with <c>promoteValues</c>/<c>promoteLongs</c>): what the legacy backup writes for collection options and index
/// specs (<c>C</c> frames, PLAN.md §7.6).
/// </summary>
/// <remarks>
/// Every number is a JavaScript number (Int32, Double, and an Int64 within ±2^53; a larger Int64 stays js-bson's
/// <c>Long</c>, whose <c>toJSON</c> is its decimal text); NaN and ±Infinity are <c>null</c>. A date is its ISO text
/// (<c>null</c> outside JavaScript's range), an ObjectId its hex text, a Decimal128 <c>{"$numberDecimal": …}</c>
/// (js-bson's <c>toJSON</c>), a regular expression <c>{}</c> (a JavaScript <c>RegExp</c>), <c>undefined</c> left out
/// of an object and <c>null</c> in an array. The other types (binary, timestamp, code, symbol, min/max key, DBRef) are
/// written as relaxed Extended JSON: they never occur in options or index specs of the legacy or the new database.
/// Strings are escaped as System.Text.Json's relaxed encoder does: the same JSON value, but characters outside the
/// basic plane (e.g. emoji) come as <c>\uD83D\uDE00</c>, which <c>JSON.stringify</c> writes as they are.
/// </remarks>
internal static class JsJson
{
    private const long MaxSafeLong = 1L << 53;

    private static readonly JsonWriterSettings RelaxedJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    public static void Write(Utf8JsonWriter writer, BsonValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    if (element.Value.BsonType == BsonType.Undefined)
                    {
                        continue;
                    }

                    writer.WritePropertyName(element.Name);
                    Write(writer, element.Value);
                }

                writer.WriteEndObject();
                break;
            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                {
                    if (item.BsonType == BsonType.Undefined)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        Write(writer, item);
                    }
                }

                writer.WriteEndArray();
                break;
            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                break;
            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;
            case BsonType.Null:
                writer.WriteNullValue();
                break;
            case BsonType.Int32:
                WriteNumber(writer, value.AsInt32);
                break;
            case BsonType.Double:
                WriteNumber(writer, value.AsDouble);
                break;
            case BsonType.Int64 when value.AsInt64 is >= -MaxSafeLong and <= MaxSafeLong:
                WriteNumber(writer, value.AsInt64);
                break;
            case BsonType.Int64:
                writer.WriteStringValue(value.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case BsonType.Decimal128:
                writer.WriteStartObject();
                writer.WriteString("$numberDecimal", value.AsDecimal128.ToString());
                writer.WriteEndObject();
                break;
            case BsonType.ObjectId:
                writer.WriteStringValue(value.AsObjectId.ToString());
                break;
            case BsonType.DateTime when JsDate.TimeClip(value.AsBsonDateTime.MillisecondsSinceEpoch) is { } time:
                writer.WriteStringValue(JsDate.ToIsoString(time));
                break;
            case BsonType.DateTime:
                writer.WriteNullValue(); // an Invalid Date's toJSON
                break;
            case BsonType.RegularExpression:
                writer.WriteStartObject();
                writer.WriteEndObject();
                break;
            default:
                writer.WriteRawValue(value.ToJson(RelaxedJson));
                break;
        }
    }

    /// <summary>The UTF-8 JSON text of <paramref name="write"/> (strings escaped only where JSON needs it).</summary>
    public static byte[] ToUtf8(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (double.IsFinite(value))
        {
            writer.WriteRawValue(JsNumber.ToString(value), skipInputValidation: true);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
