using Dib4t.Infrastructure.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>
/// Numbers are written like the legacy Node driver (js-bson 1.1.6 <c>serializeNumber</c>) and read from every BSON
/// number type (PLAN.md §4.3 rule 1, §4.4 rule 1).
/// </summary>
public sealed class JsNumberSerializerTests
{
    [Theory]
    [InlineData(0.0, BsonType.Int32)]
    [InlineData(1.0, BsonType.Int32)]
    [InlineData(-1.0, BsonType.Int32)]
    [InlineData(60.0, BsonType.Int32)]
    [InlineData(2147483647.0, BsonType.Int32)]
    [InlineData(-2147483648.0, BsonType.Int32)]
    [InlineData(2147483648.0, BsonType.Double)]
    [InlineData(-2147483649.0, BsonType.Double)]
    [InlineData(9007199254740992.0, BsonType.Double)] // 2^53: never Int64
    [InlineData(1e300, BsonType.Double)]
    [InlineData(0.5, BsonType.Double)]
    [InlineData(-0.5, BsonType.Double)]
    [InlineData(12345.67, BsonType.Double)]
    [InlineData(double.NaN, BsonType.Double)]
    [InlineData(double.PositiveInfinity, BsonType.Double)]
    [InlineData(double.NegativeInfinity, BsonType.Double)]
    [InlineData(double.Epsilon, BsonType.Double)]
    public void Writes_the_bson_type_the_legacy_driver_writes(double value, BsonType expected)
    {
        var written = Write(value);

        written.BsonType.Should().Be(expected);
        JsNumberSerializer.BsonTypeFor(value).Should().Be(expected);
        JsNumberSerializer.ToBsonValue(value).Should().Be(written);
        Read(written).Should().Be(value);
    }

    [Fact]
    public void Negative_zero_is_written_as_int32_zero_like_js_bson()
    {
        var written = Write(-0.0);

        written.Should().Be(new BsonInt32(0));
        written.BsonType.Should().Be(BsonType.Int32);
    }

    [Fact]
    public void Reads_int32_int64_double_and_decimal128()
    {
        Read(new BsonInt32(42)).Should().Be(42);
        Read(new BsonInt64(5_000_000_000)).Should().Be(5_000_000_000);
        Read(new BsonInt64(9_007_199_254_740_993)).Should().Be(9_007_199_254_740_992); // rounds, as in JavaScript
        Read(new BsonDouble(0.5)).Should().Be(0.5);
        Read(new BsonDouble(-3)).Should().Be(-3);
        Read(new BsonDecimal128(Decimal128.Parse("12.25"))).Should().Be(12.25);
    }

    [Theory]
    [InlineData("\"10\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{ \"$date\": \"2024-01-01T00:00:00Z\" }")]
    public void Other_bson_types_are_a_format_error(string json)
    {
        var act = () => Read(BsonDocument.Parse("{ \"v\": " + json + " }")["v"]);

        act.Should().Throw<FormatException>().WithMessage("Cannot read a number from BSON type *");
    }

    [Fact]
    public void Nullable_serializer_keeps_null()
    {
        var document = new BsonDocument("v", BsonNull.Value);
        using var reader = new BsonDocumentReader(document);
        reader.ReadStartDocument();
        reader.ReadName();

        JsNumberSerializer.Nullable.Deserialize(BsonDeserializationContext.CreateRoot(reader)).Should().BeNull();
    }

    private static BsonValue Write(double value)
    {
        var document = new BsonDocument();
        using (var writer = new BsonDocumentWriter(document))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            JsNumberSerializer.Instance.Serialize(BsonSerializationContext.CreateRoot(writer), value);
            writer.WriteEndDocument();
        }

        return document["v"];
    }

    private static double Read(BsonValue value)
    {
        using var reader = new BsonDocumentReader(new BsonDocument("v", value));
        reader.ReadStartDocument();
        reader.ReadName();
        return JsNumberSerializer.Instance.Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }
}
