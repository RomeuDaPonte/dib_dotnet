using System.Net;
using System.Net.Http.Headers;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// PLAN.md §4.0 / §11.3: a legacy <c>.dibbak</c> restored through <c>POST /api/database/restore</c> on
/// <c>mongo:9.0.8</c> holds every document <b>byte for byte</b> (same <c>_id</c>s, fields, field order, values and
/// BSON types: raw BSON, no re-serialization), in the same collections, with the backup's indexes.
/// Compared with the independent oracle reader (<see cref="DibbakFile"/>), not the app's own reader.
/// </summary>
public sealed class LegacyBackupImportTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    /// <summary>Path of a full, real production backup (kept outside the repo); the size test is skipped without it.</summary>
    public const string FullBackupVariable = "DIB4T_FULL_BACKUP";

    private readonly HttpClient _client = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymized_production_backup_imports_byte_for_byte() =>
        await ShouldImportByteForByteAsync(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));

    [Fact]
    public async Task Full_production_backup_imports_byte_for_byte()
    {
        var path = Environment.GetEnvironmentVariable(FullBackupVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path), $"set {FullBackupVariable} to a real .dibbak (never committed) to run this before a release");

        await ShouldImportByteForByteAsync(await File.ReadAllBytesAsync(path!, Cancellation));
    }

    [Fact]
    public async Task Every_bson_type_and_legacy_shape_survives_the_import()
    {
        var file = DibbakTestWriter.Valid(
            ("budgets", [EveryBsonType(), OldShape()]),
            ("tasks", [new BsonDocument { { "_id", ObjectId.Parse("6000000000000000000000a1") }, { "price", 10 } },
                       new BsonDocument { { "_id", ObjectId.Parse("6000000000000000000000a2") }, { "price", 10.0 } },
                       new BsonDocument { { "_id", ObjectId.Parse("6000000000000000000000a3") }, { "price", 10L } }]),
            ("empty", [])).ToFile();

        await ShouldImportByteForByteAsync(file);
    }

    [Fact]
    public async Task Index_specs_from_mongodb_4_1_4_are_recreated()
    {
        // Shape of collection.indexes() on 4.1.4: v, ns and background are in every spec.
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame(
                "users",
                indexes: """[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.users"},{"v":2,"unique":true,"key":{"email":1},"name":"email_1","ns":"dib4t.users","background":true}]""")
            .DocumentFrame(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "email", "a@dib4t.local" } })
            .EndFrame(1, 1).ToFile();

        await ShouldImportByteForByteAsync(file);

        var indexes = await (await app.Database.GetCollection<BsonDocument>("users").Indexes.ListAsync(Cancellation)).ToListAsync(Cancellation);
        indexes.Single(i => i["name"] == "email_1")["unique"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task Importing_again_replaces_the_previous_import()
    {
        await PostAsync(DibbakTestWriter.Valid(("first", [new BsonDocument("_id", 1)])).ToFile());

        await ShouldImportByteForByteAsync(DibbakTestWriter.Valid(("second", [new BsonDocument("_id", 2)])).ToFile());
    }

    private async Task ShouldImportByteForByteAsync(byte[] file)
    {
        var expected = DibbakFile.Parse(file);

        using var response = await PostAsync(file);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
        var names = await (await app.Database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation);
        names.Where(n => !n.StartsWith("system.", StringComparison.Ordinal))
            .Should().BeEquivalentTo(expected.Collections.Select(c => c.Name));

        foreach (var collection in expected.Collections)
        {
            var stored = await app.Database.GetCollection<RawBsonDocument>(collection.Name)
                .Find(FilterDefinition<RawBsonDocument>.Empty).Sort(new BsonDocument("$natural", 1)).ToListAsync(Cancellation);
            try
            {
                var storedBytes = stored.Select(Bytes).ToList();
                stored.Should().HaveCount(collection.Documents.Count, "collection {0}", collection.Name);
                for (var i = 0; i < collection.Documents.Count; i++)
                {
                    if (!storedBytes[i].AsSpan().SequenceEqual(collection.Documents[i]))
                    {
                        var diff = DbSnapshot.From(new Dictionary<string, IEnumerable<BsonDocument>> { [collection.Name] = [BsonSerializer(storedBytes[i])] })
                            .DiffFrom(DbSnapshot.From(new Dictionary<string, IEnumerable<BsonDocument>> { [collection.Name] = [BsonSerializer(collection.Documents[i])] }));
                        Assert.Fail($"{collection.Name} document {i} differs from the backup: {string.Join("; ", diff)}");
                    }
                }
            }
            finally
            {
                stored.ForEach(d => d.Dispose());
            }
        }
    }

    private static byte[] Bytes(RawBsonDocument document)
    {
        var bytes = new byte[document.Slice.Length];
        document.Slice.GetBytes(0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static BsonDocument BsonSerializer(byte[] bytes) => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(bytes);

    private async Task<HttpResponseMessage> PostAsync(byte[] file)
    {
        using var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
    }

    /// <summary>Every BSON type MongoDB 9.0 stores, incl. the deprecated ones a 4.1.4 database can hold.</summary>
    private static BsonDocument EveryBsonType() => new()
    {
        { "_id", ObjectId.Parse("600000000000000000000001") },
        { "double", 43.5 },
        { "doubleWhole", 10.0 },
        { "negativeZero", -0.0 },
        { "nan", double.NaN },
        { "string", "Orçamento" },
        { "nonNfc", "Orçamento" },
        { "emoji", "🙂" },
        { "embedded", new BsonDocument { { "z", 1 }, { "a", 2 }, { "a.b", 3 } } },
        { "array", new BsonArray { 1, 2.5, "x", BsonNull.Value, new BsonArray() } },
        { "binary", new BsonBinaryData([1, 2, 3]) },
        { "uuid", new BsonBinaryData(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), GuidRepresentation.Standard) },
        { "undefined", BsonUndefined.Value },
        { "objectId", ObjectId.Parse("5f1e3c2a9b1d4e0012345678") },
        { "boolean", true },
        { "date", new BsonDateTime(1_700_000_000_123) },
        { "dateBefore1970", new BsonDateTime(-86_400_000) },
        { "null", BsonNull.Value },
        { "regex", new BsonRegularExpression("^orç", "i") },
        { "javascript", new BsonJavaScript("function () { return 1; }") },
        { "symbol", BsonSymbol.Create("sym") },
        { "javascriptWithScope", new BsonJavaScriptWithScope("x", new BsonDocument("x", 1)) },
        { "int32", 35 },
        { "timestamp", new BsonTimestamp(1_700_000_000, 7) },
        { "int64", 3_000_000_000L },
        { "decimal128", new BsonDecimal128(Decimal128.Parse("1234.5600")) },
        { "minKey", BsonMinKey.Value },
        { "maxKey", BsonMaxKey.Value },
    };

    /// <summary>An old document: missing later fields, an unknown field, <c>__v</c> as Int32, fields in an unusual order.</summary>
    private static BsonDocument OldShape() => new()
    {
        { "_id", ObjectId.Parse("600000000000000000000002") },
        { "zzUnknownLegacyField", new BsonDocument("kept", true) },
        { "budgetNumber", "18-0001" },
        { "__v", 0 },
    };
}
