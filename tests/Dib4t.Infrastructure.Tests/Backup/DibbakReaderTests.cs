using Dib4t.Application.Database;
using Dib4t.Infrastructure.Backup;
using Dib4t.Tests.Common;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Tests.Backup;

/// <summary>
/// The first restore pass (gunzip + <c>parseBackup</c> port): every legacy message of <c>helpers/backupFormat.js</c>
/// for the input that produces it in the legacy code, plus the checks the new app adds before dropping anything.
/// </summary>
public sealed class DibbakReaderTests
{
    private static readonly BsonDocument SampleDocument = new() { { "_id", ObjectId.Parse("5f1e3c2a9b1d4e0012345678") }, { "name", "x" } };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Production_fixture_is_valid()
    {
        await using var file = File.OpenRead(Fixtures.ProductionBackup);

        var summary = await MongoDatabaseBackupService.CheckAsync(file, Cancellation);

        summary.Collections.Should().Be(8);
        summary.Documents.Should().Be(30_754);
        summary.CreatedAt!.Value.GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        summary.Database!.Value.GetString().Should().Be("dib4t");
    }

    [Fact]
    public async Task Valid_backup_reports_header_values_and_totals()
    {
        var summary = await ValidateAsync(DibbakTestWriter.Valid(("a", [SampleDocument]), ("b", [])).ToFile());

        summary.Should().BeEquivalentTo(new { Collections = 2, Documents = 1L });
        summary.CreatedAt!.Value.GetString().Should().Be("2026-11-11T16:15:31.123Z");
    }

    // gunzip failures (legacy: zlib error → "não é um backup válido")

    [Fact]
    public async Task Text_that_is_not_gzip_is_not_a_backup() =>
        // characterization step db-restore-garbage
        await ShouldRejectAsync("this is not a backup"u8.ToArray(), BackupMessages.NotAValidBackup);

    [Fact]
    public async Task First_half_of_a_backup_is_not_a_backup()
    {
        // characterization step db-restore-truncated: GZipStream alone would return the partial data without an error
        var file = await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation);

        await ShouldRejectAsync(file[..(file.Length / 2)], BackupMessages.NotAValidBackup);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Backup_missing_the_end_of_its_gzip_trailer_is_not_a_backup(int missingBytes)
    {
        var file = DibbakTestWriter.Valid(("a", [SampleDocument])).ToFile();

        await ShouldRejectAsync(file[..^missingBytes], BackupMessages.NotAValidBackup);
    }

    [Fact]
    public async Task Corrupted_gzip_data_is_not_a_backup()
    {
        var file = DibbakTestWriter.Valid(("a", [SampleDocument])).ToFile();
        file[^6] ^= 0xFF; // CRC32

        await ShouldRejectAsync(file, BackupMessages.NotAValidBackup);
    }

    [Fact]
    public async Task Gzip_error_wins_over_a_format_error()
    {
        // Legacy gunzips everything before parsing: a cut-off file whose first frame is wrong still reports the gzip error.
        var file = new DibbakTestWriter().Magic().Json(DibbakTestWriter.End, "{}").Raw(new byte[100_000]).ToFile();

        await ShouldRejectAsync(file[..^20], BackupMessages.NotAValidBackup);
    }

    // parseBackup

    [Theory]
    [InlineData("")]
    [InlineData("DIBBAK0")]
    [InlineData("DIBBAK02")]
    public async Task Wrong_magic_is_not_a_backup(string magic) =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic(magic).ToFile(), BackupMessages.NotAValidBackup);

    [Fact]
    public async Task Magic_only_is_incomplete() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().ToFile(), BackupMessages.Incomplete);

    [Fact]
    public async Task Partial_frame_prefix_is_incomplete() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Raw([0x43, 1, 0]).ToFile(), BackupMessages.Incomplete);

    [Fact]
    public async Task Frame_longer_than_the_data_is_incomplete() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().Frame(DibbakTestWriter.Header, "{}"u8.ToArray(), declaredLength: 100).ToFile(), BackupMessages.Incomplete);

    [Fact]
    public async Task Oversized_document_frame_that_is_cut_off_is_incomplete() =>
        await ShouldRejectAsync(
            new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").Frame(DibbakTestWriter.Document, new byte[1000], declaredLength: 20_000_000).ToFile(),
            BackupMessages.Incomplete);

    [Fact]
    public async Task First_frame_must_be_the_header() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().CollectionFrame("a").ToFile(), BackupMessages.MissingHeader);

    [Fact]
    public async Task Header_that_is_not_json_is_corrupted() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().Json(DibbakTestWriter.Header, "{format:").ToFile(), BackupMessages.CorruptedHeader);

    [Theory]
    [InlineData("""{"format":"other","version":1}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""["dib4t-backup"]""")]
    [InlineData("0")]
    [InlineData("\"dib4t-backup\"")]
    public async Task Header_of_another_format_is_not_a_backup(string header) =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame(header).ToFile(), BackupMessages.NotAValidBackup);

    [Fact]
    public async Task Header_json_null_fails_like_the_legacy_TypeError()
    {
        // Legacy: `null.format` throws a TypeError → 500 "Erro ao restaurar o backup." (before anything is dropped).
        var file = new DibbakTestWriter().Magic().HeaderFrame("null").ToFile();

        var act = () => ValidateAsync(file);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Last_duplicate_header_key_wins_like_JSON_parse()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame("""{"format":"x","format":"dib4t-backup"}""").EndFrame(0, 0).ToFile();

        (await ValidateAsync(file)).Collections.Should().Be(0);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    public async Task Header_of_a_newer_version_is_rejected(string version) =>
        await ShouldRejectAsync(
            new DibbakTestWriter().Magic().HeaderFrame($$"""{"format":"dib4t-backup","version":{{version}}}""").EndFrame(0, 0).ToFile(),
            BackupMessages.NewerVersion);

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    public async Task Header_version_not_greater_than_1_is_accepted(string version)
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame($$"""{"format":"dib4t-backup","version":{{version}}}""").EndFrame(0, 0).ToFile();

        (await ValidateAsync(file)).Documents.Should().Be(0);
    }

    [Fact]
    public async Task Header_without_createdAt_reports_no_value()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame("""{"format":"dib4t-backup"}""").EndFrame(0, 0).ToFile();

        var summary = await ValidateAsync(file);

        summary.CreatedAt.Should().BeNull();
        summary.Database.Should().BeNull();
    }

    [Fact]
    public async Task Second_header_is_rejected() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().HeaderFrame().ToFile(), BackupMessages.DuplicateHeader);

    [Fact]
    public async Task Collection_that_is_not_json_is_corrupted() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Json(DibbakTestWriter.Collection, "{").ToFile(), BackupMessages.CorruptedCollection);

    [Theory]
    [InlineData("""{"options":{}}""")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":5}""")]
    [InlineData("null")]
    [InlineData("\"budgets\"")]
    public async Task Collection_without_a_name_is_invalid(string json) =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Json(DibbakTestWriter.Collection, json).ToFile(), BackupMessages.InvalidCollection);

    [Theory]
    [InlineData("system.views")]
    [InlineData("a$b")]
    public async Task Collection_name_mongodb_cannot_create_is_rejected_before_anything_is_dropped(string name) =>
        // New-app check: legacy would drop the database and then fail on createCollection (500, half restored).
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame(name).EndFrame(1, 0).ToFile(), BackupMessages.InvalidCollection);

    [Fact]
    public async Task Duplicate_collection_is_rejected_before_anything_is_dropped() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").CollectionFrame("a").EndFrame(2, 0).ToFile(), BackupMessages.InvalidCollection);

    [Theory]
    [InlineData("\"capped\"")]
    [InlineData("[1]")]
    public async Task Collection_options_that_are_not_an_object_are_rejected(string options) =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a", options).EndFrame(1, 0).ToFile(), BackupMessages.InvalidCollection);

    [Fact]
    public async Task Index_whose_key_is_not_an_object_is_rejected() =>
        await ShouldRejectAsync(
            new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a", indexes: """[{"key":"name_1","name":"name_1"}]""").EndFrame(1, 0).ToFile(),
            BackupMessages.InvalidCollection);

    [Fact]
    public async Task Document_before_any_collection_is_rejected() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().DocumentFrame(SampleDocument).ToFile(), BackupMessages.DocumentWithoutCollection);

    [Fact]
    public async Task Document_shorter_than_5_bytes_is_corrupted() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").Frame(DibbakTestWriter.Document, [4, 0, 0, 0]).ToFile(), BackupMessages.CorruptedDocument);

    [Fact]
    public async Task Document_whose_bson_length_differs_from_the_frame_is_corrupted()
    {
        var bson = SampleDocument.ToBson();
        bson[0]++;

        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").Frame(DibbakTestWriter.Document, bson).ToFile(), BackupMessages.CorruptedDocument);
    }

    [Fact]
    public async Task Document_that_is_not_valid_bson_is_rejected_before_anything_is_dropped()
    {
        // New-app check: legacy would fail in bson.deserialize after dropping the database.
        var bson = SampleDocument.ToBson();
        bson[4] = 0x7E; // unknown element type

        await ShouldRejectAsync(
            new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").Frame(DibbakTestWriter.Document, bson).EndFrame(1, 1).ToFile(),
            BackupMessages.CorruptedDocument);
    }

    [Fact]
    public async Task Document_with_invalid_utf8_is_accepted_and_kept_as_it_is()
    {
        // Legacy js-bson decodes invalid UTF-8 leniently; mongod stores it. Rejecting it would refuse a file legacy restores.
        var bson = new BsonDocument("s", "ab").ToBson();
        bson[^3] = 0xFF;

        var file = new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").Frame(DibbakTestWriter.Document, bson).EndFrame(1, 1).ToFile();

        (await ValidateAsync(file)).Documents.Should().Be(1);
    }

    [Fact]
    public async Task End_that_is_not_json_is_corrupted() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Json(DibbakTestWriter.End, "}").ToFile(), BackupMessages.CorruptedEnd);

    [Fact]
    public async Task Missing_end_frame_is_incomplete() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").DocumentFrame(SampleDocument).ToFile(), BackupMessages.Incomplete);

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("\"\"")]
    public async Task Falsy_end_frame_counts_as_missing(string end) =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Json(DibbakTestWriter.End, end).ToFile(), BackupMessages.Incomplete);

    [Fact]
    public async Task Falsy_end_frame_can_be_followed_by_more_frames_like_legacy()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame().Json(DibbakTestWriter.End, "null").CollectionFrame("a").EndFrame(1, 0).ToFile();

        (await ValidateAsync(file)).Collections.Should().Be(1);
    }

    [Fact]
    public async Task Data_after_the_end_frame_is_rejected() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().EndFrame(0, 0).Raw([0]).ToFile(), BackupMessages.UnexpectedDataAfterEnd);

    [Theory]
    [InlineData("""{"collections":2,"documents":1}""")]
    [InlineData("""{"collections":1,"documents":2}""")]
    [InlineData("""{"collections":"1","documents":1}""")]
    [InlineData("""{"collections":1}""")]
    [InlineData("true")]
    public async Task End_counts_that_do_not_match_are_rejected(string end) =>
        await ShouldRejectAsync(
            new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").DocumentFrame(SampleDocument).Json(DibbakTestWriter.End, end).ToFile(),
            BackupMessages.CountsMismatch);

    [Fact]
    public async Task End_counts_compare_as_numbers()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame().CollectionFrame("a").DocumentFrame(SampleDocument)
            .Json(DibbakTestWriter.End, """{"collections":1.0,"documents":1e0}""").ToFile();

        (await ValidateAsync(file)).Documents.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_frame_type_is_corrupted() =>
        await ShouldRejectAsync(new DibbakTestWriter().Magic().HeaderFrame().Json((byte)'X', "{}").ToFile(), BackupMessages.UnknownFrame);

    // Collection frames → MongoDB commands

    [Fact]
    public async Task Collection_options_and_indexes_become_the_legacy_commands()
    {
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame(
                "a",
                options: """{"capped":true,"size":4096,"max":1.5,"w":1,"strict":true,"validationLevel":null}""",
                indexes: """[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.a"},{"v":2,"unique":true,"key":{"name":1.0,"big":3000000000},"name":"name_1","ns":"dib4t.a","background":true},{"name":"no_key"},"text"]""")
            .EndFrame(1, 0).ToFile();
        var visitor = new RecordingVisitor();

        await ReadAsync(file, visitor);

        var collection = visitor.Collections.Should().ContainSingle().Subject;
        // Canonical Extended JSON: BSON types are part of the comparison ({"$numberInt"} vs {"$numberDouble"}).
        Canonical(collection.CreateOptions).Should().Be(Canonical(new BsonDocument { { "capped", true }, { "size", 4096 }, { "max", 1.5 } }));
        Canonical(collection.IndexSpecs.Should().ContainSingle().Subject).Should().Be(Canonical(new BsonDocument
        {
            { "unique", true },
            // JSON 1.0 is the JS number 1 (Int32); outside the Int32 range the legacy driver writes a Double.
            { "key", new BsonDocument { { "name", 1 }, { "big", 3000000000d } } },
            { "name", "name_1" },
        }));
        // background: ignored since MongoDB 4.2, left out for 9.0 (MongoCompatibility) and reported.
        collection.Adjustments.Should().BeEquivalentTo(new[] { new RestoreAdjustment("a", "name_1", ["background"]) }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Documents_are_passed_on_byte_for_byte()
    {
        var bson = SampleDocument.ToBson();
        var visitor = new RecordingVisitor();

        await ReadAsync(DibbakTestWriter.Valid(("a", [SampleDocument])).ToFile(), visitor);

        visitor.Documents.Should().ContainSingle().Which.Should().Equal(bson);
    }

    private static string Canonical(BsonDocument document) =>
        document.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson });

    private static async Task<DibbakSummary> ValidateAsync(byte[] file)
    {
        using var stream = new MemoryStream(file);
        return await MongoDatabaseBackupService.CheckAsync(stream, Cancellation);
    }

    private static async Task ReadAsync(byte[] file, IDibbakVisitor visitor)
    {
        using var stream = new MemoryStream(file);
        await using var content = new VerifiedGzipReadStream(stream);
        await DibbakReader.ReadAsync(content, visitor, validateDocuments: true, Cancellation);
    }

    private static async Task ShouldRejectAsync(byte[] file, string message)
    {
        var act = () => ValidateAsync(file);

        (await act.Should().ThrowAsync<BackupFormatException>()).Which.Message.Should().Be(message);
    }

    private sealed class RecordingVisitor : IDibbakVisitor
    {
        public List<DibbakCollection> Collections { get; } = [];

        public List<byte[]> Documents { get; } = [];

        public ValueTask OnCollectionAsync(DibbakCollection collection, CancellationToken cancellationToken)
        {
            Collections.Add(collection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDocumentAsync(byte[] document, CancellationToken cancellationToken)
        {
            Documents.Add(document);
            return ValueTask.CompletedTask;
        }
    }
}
