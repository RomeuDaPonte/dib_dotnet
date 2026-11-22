using System.Text;
using Dib4t.Infrastructure.Backup;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>
/// <see cref="JsJson"/>: <c>JSON.stringify</c> of what the legacy driver (js-bson 1.x, promoted values) reads, the
/// text of a backup's collection options and index specs.
/// </summary>
public sealed class JsJsonTests
{
    private static string Stringify(BsonValue value) => Encoding.UTF8.GetString(JsJson.ToUtf8(writer => JsJson.Write(writer, value)));

    public static TheoryData<string, BsonValue, string> Values => new()
    {
        { "Int32", 1, "1" },
        { "integral Double", 1.0, "1" },
        { "Double", 0.1, "0.1" },
        { "small Double", 1e-7, "1e-7" },
        { "large Double", 1e21, "1e+21" },
        { "NaN", double.NaN, "null" },
        { "Infinity", double.PositiveInfinity, "null" },
        { "negative zero", -0.0, "0" },
        { "Int64 promoted", 4096L, "4096" },
        { "Int64 at 2^53", 1L << 53, "9007199254740992" },
        { "Int64 beyond 2^53 (Long.toJSON)", (1L << 53) + 1, "\"9007199254740993\"" },
        { "Decimal128", new Decimal128(1.50m), """{"$numberDecimal":"1.50"}""" },
        { "string", "pt", "\"pt\"" },
        { "string with quote, backslash, control and non-ASCII", "a\"b\\c\nç€", "\"a\\\"b\\\\c\\nç€\"" },
        { "emoji: escaped (JSON.stringify writes it as it is; the same JSON value)", "😀", "\"\\uD83D\\uDE00\"" },
        { "Boolean", true, "true" },
        { "null", BsonNull.Value, "null" },
        { "ObjectId", ObjectId.Parse("5f1e9a2b3c4d5e6f70819203"), "\"5f1e9a2b3c4d5e6f70819203\"" },
        { "date", new BsonDateTime(1_794_650_400_123), "\"2026-11-14T10:00:00.123Z\"" },
        { "date before 1970", new BsonDateTime(-1), "\"1969-12-31T23:59:59.999Z\"" },
        { "date beyond JavaScript", new BsonDateTime(long.MaxValue), "null" },
        { "regular expression (a RegExp)", new BsonRegularExpression("^a", "i"), "{}" },
        { "document in field order", new BsonDocument { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" } }, """{"v":2,"key":{"_id":1},"name":"_id_"}""" },
        { "undefined left out of a document", new BsonDocument { { "a", BsonUndefined.Value }, { "b", 1 } }, """{"b":1}""" },
        { "array (undefined is null)", new BsonArray { 1, "x", BsonUndefined.Value, new BsonArray() }, """[1,"x",null,[]]""" },
        { "empty document", new BsonDocument(), "{}" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Writes_the_value_as_JSON_stringify_does(string because, BsonValue value, string json) =>
        Stringify(value).Should().Be(json, because);

    [Fact]
    public void Other_types_are_relaxed_extended_json_that_never_occurs_in_options_or_indexes() =>
        Stringify(new BsonTimestamp(1, 2)).Should().Be("""{ "$timestamp" : { "t" : 1, "i" : 2 } }""");
}
