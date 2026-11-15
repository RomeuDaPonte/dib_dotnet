using System.Text.Json;
using System.Text.Json.Serialization;
using Dib4t.SharedKernel;

namespace Dib4t.Server.Http;

/// <summary>
/// Numbers in API answers as Express writes them (<c>JSON.stringify</c>, i.e. JavaScript's number text): <c>1e-7</c>,
/// <c>1e+21</c> where .NET would write <c>1E-07</c>, <c>1E+21</c>. NaN and ±Infinity are <c>null</c>, as in JavaScript.
/// </summary>
public sealed class JsNumberJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
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
