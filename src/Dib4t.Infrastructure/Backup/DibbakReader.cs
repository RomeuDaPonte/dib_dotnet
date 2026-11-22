using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Dib4t.Application.Database;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Dib4t.Infrastructure.Backup;

/// <summary>A collection of the backup, with the command parts ready for MongoDB.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="CreateOptions">Fields added to the <c>create</c> command (legacy <c>createCollection(name, options)</c>).</param>
/// <param name="IndexSpecs">Index specs for <c>createIndexes</c>, without <c>_id_</c>, <c>v</c> and <c>ns</c>.</param>
/// <param name="Adjustments">What <see cref="MongoCompatibility"/> left out of the options and index specs for MongoDB 9.0.</param>
internal sealed record DibbakCollection(
    string Name, BsonDocument CreateOptions, IReadOnlyList<BsonDocument> IndexSpecs, IReadOnlyList<RestoreAdjustment> Adjustments);

/// <summary>Header values and totals of a backup that passed every check.</summary>
internal sealed record DibbakSummary(JsonElement? CreatedAt, JsonElement? Database, int Collections, long Documents);

/// <summary>Receives the content of a backup while it is read.</summary>
internal interface IDibbakVisitor
{
    ValueTask OnCollectionAsync(DibbakCollection collection, CancellationToken cancellationToken);

    /// <summary>One raw BSON document of the last collection (the array is not reused).</summary>
    ValueTask OnDocumentAsync(byte[] document, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and validates the decompressed <c>.dibbak</c> content frame by frame, as a stream (one frame in memory at a
/// time). Port of legacy <c>parseBackup</c> (<c>helpers/backupFormat.js</c>): same checks, same order, same messages,
/// including the JavaScript semantics they depend on (truthiness, last duplicate JSON key wins, strict number equality).
/// </summary>
/// <remarks>
/// Checks the legacy app does not make before it drops the database, and that would make it fail half-way (500 with a
/// partly restored database): a document that is not valid BSON or larger than MongoDB allows
/// ("Documento corrompido no backup."), and a collection that cannot be created as given: duplicate, <c>system.*</c>
/// or otherwise invalid name, options that are not an object, an index whose <c>key</c> is not an object
/// ("Coleção inválida no backup."). PLAN.md §4.0 item 4: a damaged file leaves the database unchanged.
/// </remarks>
internal static class DibbakReader
{
    /// <summary>Fields the legacy driver (mongodb 3.7.4 <c>CreateCollectionOperation</c>) never sends with <c>create</c>.</summary>
    private static readonly HashSet<string> IllegalCreateFields = new(StringComparer.Ordinal)
    {
        "w", "wtimeout", "j", "fsync", "autoIndexId", "strict", "serializeFunctions", "pkFactory", "raw",
        "readPreference", "session", "readConcern", "writeConcern",
    };

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 256 };

    private static readonly BsonBinaryReaderSettings LenientBson = new() { Encoding = Utf8Encodings.Lenient };

    /// <param name="decompressed">The content after gunzip.</param>
    /// <param name="visitor">Gets every collection and document, or <see langword="null"/> to validate only.</param>
    /// <param name="validateDocuments">Parse each document fully (first pass); the second pass trusts the first.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="BackupFormatException">The file is not a valid backup (legacy message).</exception>
    public static async Task<DibbakSummary> ReadAsync(
        Stream decompressed, IDibbakVisitor? visitor, bool validateDocuments, CancellationToken cancellationToken)
    {
        var magic = new byte[DibbakFormat.Magic.Length];
        var read = await decompressed.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, cancellationToken);
        if (read < magic.Length || !magic.AsSpan().SequenceEqual(DibbakFormat.Magic))
        {
            throw new BackupFormatException(BackupMessages.NotAValidBackup);
        }

        var state = new ReaderState();
        var prefix = new byte[DibbakFormat.FramePrefixLength];
        while (true)
        {
            if (state.EndIsTruthy)
            {
                if (await decompressed.ReadAtLeastAsync(prefix.AsMemory(0, 1), 1, throwOnEndOfStream: false, cancellationToken) > 0)
                {
                    throw new BackupFormatException(BackupMessages.UnexpectedDataAfterEnd);
                }

                break;
            }

            read = await decompressed.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (read < prefix.Length)
            {
                throw new BackupFormatException(BackupMessages.Incomplete);
            }

            var type = prefix[0];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(1));
            var payload = await ReadPayloadAsync(decompressed, type, length, cancellationToken);

            if (!state.HasHeader && type != DibbakFormat.HeaderFrame)
            {
                throw new BackupFormatException(BackupMessages.MissingHeader);
            }

            switch (type)
            {
                case DibbakFormat.HeaderFrame:
                    ReadHeader(state, payload);
                    break;

                case DibbakFormat.CollectionFrame:
                    var collection = ReadCollection(state, payload);
                    if (visitor is not null)
                    {
                        await visitor.OnCollectionAsync(collection, cancellationToken);
                    }

                    break;

                case DibbakFormat.DocumentFrame:
                    CheckDocument(state, payload, length, validateDocuments);
                    state.Documents++;
                    if (visitor is not null)
                    {
                        await visitor.OnDocumentAsync(payload!, cancellationToken);
                    }

                    break;

                case DibbakFormat.EndFrame:
                    var end = ParseJson(payload, BackupMessages.CorruptedEnd);
                    state.End = end;
                    state.EndIsTruthy = IsTruthy(end);
                    break;

                default:
                    throw new BackupFormatException(BackupMessages.UnknownFrame);
            }
        }

        if (!state.EndIsTruthy)
        {
            throw new BackupFormatException(BackupMessages.Incomplete);
        }

        if (!NumberEquals(LastProperty(state.End, "collections"), state.CollectionNames.Count)
            || !NumberEquals(LastProperty(state.End, "documents"), state.Documents))
        {
            throw new BackupFormatException(BackupMessages.CountsMismatch);
        }

        return new DibbakSummary(state.CreatedAt, state.Database, state.CollectionNames.Count, state.Documents);
    }

    /// <summary>
    /// The frame payload, or <see langword="null"/> when it is larger than the frame type allows (it is then read and
    /// discarded, so a cut-off file still reports "incompleto" first, like legacy).
    /// </summary>
    private static async Task<byte[]?> ReadPayloadAsync(Stream stream, byte type, uint length, CancellationToken cancellationToken)
    {
        var max = type == DibbakFormat.DocumentFrame ? DibbakFormat.MaxDocumentLength : DibbakFormat.MaxJsonFrameLength;
        if (length <= max)
        {
            var payload = new byte[length];
            if (await stream.ReadAtLeastAsync(payload, payload.Length, throwOnEndOfStream: false, cancellationToken) < payload.Length)
            {
                throw new BackupFormatException(BackupMessages.Incomplete);
            }

            return payload;
        }

        var skip = new byte[81920];
        var remaining = (long)length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new BackupFormatException(BackupMessages.Incomplete);
            }

            remaining -= read;
        }

        return null;
    }

    private static void ReadHeader(ReaderState state, byte[]? payload)
    {
        if (state.HasHeader)
        {
            throw new BackupFormatException(BackupMessages.DuplicateHeader);
        }

        var header = ParseJson(payload, BackupMessages.CorruptedHeader);
        if (header.ValueKind == JsonValueKind.Null)
        {
            // Legacy reads `null.format` → TypeError → 500 "Erro ao restaurar o backup." (still before anything is dropped).
            throw new InvalidOperationException("The backup header is JSON null.");
        }

        if (LastProperty(header, "format") is not { ValueKind: JsonValueKind.String } format
            || format.GetString() != DibbakFormat.FormatName)
        {
            throw new BackupFormatException(BackupMessages.NotAValidBackup);
        }

        if (LastProperty(header, "version") is { } version && JsNumber(version) > DibbakFormat.FormatVersion)
        {
            throw new BackupFormatException(BackupMessages.NewerVersion);
        }

        state.HasHeader = true;
        state.CreatedAt = LastProperty(header, "createdAt");
        state.Database = LastProperty(header, "database");
    }

    private static DibbakCollection ReadCollection(ReaderState state, byte[]? payload)
    {
        var info = ParseJson(payload, BackupMessages.CorruptedCollection);
        if (info.ValueKind != JsonValueKind.Object
            || LastProperty(info, "name") is not { ValueKind: JsonValueKind.String } nameElement
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            throw new BackupFormatException(BackupMessages.InvalidCollection);
        }

        if (name.StartsWith("system.", StringComparison.Ordinal) || name.Contains('$', StringComparison.Ordinal)
            || name.Contains('\0', StringComparison.Ordinal) || !state.CollectionNames.Add(name))
        {
            throw new BackupFormatException(BackupMessages.InvalidCollection);
        }

        try
        {
            var options = new BsonDocument();
            switch (LastProperty(info, "options"))
            {
                case { ValueKind: JsonValueKind.Object } value:
                    foreach (var option in ToBsonDocument(value))
                    {
                        if (!option.Value.IsBsonNull && !IllegalCreateFields.Contains(option.Name))
                        {
                            options[option.Name] = option.Value;
                        }
                    }

                    break;
                case { } value when IsTruthy(value):
                    throw new BackupFormatException(BackupMessages.InvalidCollection);
            }

            var adjustments = new List<RestoreAdjustment>();
            options = MongoCompatibility.CleanOptions(name, options, adjustments);
            var indexes = new List<BsonDocument>();
            if (LastProperty(info, "indexes") is { ValueKind: JsonValueKind.Array } indexArray)
            {
                foreach (var index in indexArray.EnumerateArray())
                {
                    // Legacy toIndexSpecs: keep `i && i.name !== "_id_" && i.key`, drop `v` and `ns`.
                    if (index.ValueKind != JsonValueKind.Object
                        || LastProperty(index, "key") is not { } key || !IsTruthy(key)
                        || LastProperty(index, "name") is { ValueKind: JsonValueKind.String } indexName && indexName.GetString() == "_id_")
                    {
                        continue;
                    }

                    if (key.ValueKind != JsonValueKind.Object)
                    {
                        throw new BackupFormatException(BackupMessages.InvalidCollection);
                    }

                    var spec = ToBsonDocument(index);
                    spec.Remove("v");
                    spec.Remove("ns");
                    if (MongoCompatibility.CleanIndex(name, spec, adjustments, options.Contains("collation")) is { } cleaned)
                    {
                        indexes.Add(cleaned);
                    }
                }
            }

            return new DibbakCollection(name, options, indexes, adjustments);
        }
        catch (ArgumentException)
        {
            // e.g. a field name MongoDB.Bson cannot hold
            throw new BackupFormatException(BackupMessages.InvalidCollection);
        }
    }

    private static void CheckDocument(ReaderState state, byte[]? payload, uint length, bool validate)
    {
        if (state.CollectionNames.Count == 0)
        {
            throw new BackupFormatException(BackupMessages.DocumentWithoutCollection);
        }

        if (payload is null || length < 5 || BinaryPrimitives.ReadInt32LittleEndian(payload) != length)
        {
            throw new BackupFormatException(BackupMessages.CorruptedDocument);
        }

        if (!validate)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BsonBinaryReader(stream, LenientBson);
            BsonDocumentSerializer.Instance.Deserialize(BsonDeserializationContext.CreateRoot(reader));
            if (stream.Position != payload.Length)
            {
                throw new BackupFormatException(BackupMessages.CorruptedDocument);
            }
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or BsonException or ArgumentException or InvalidOperationException)
        {
            throw new BackupFormatException(BackupMessages.CorruptedDocument);
        }
    }

    private static JsonElement ParseJson(byte[]? payload, string message)
    {
        if (payload is null)
        {
            throw new BackupFormatException(message);
        }

        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BackupFormatException(message);
        }
    }

    /// <summary>JSON.parse keeps the last of duplicate keys; <see langword="null"/> when missing or not an object.</summary>
    private static JsonElement? LastProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? last = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                last = property.Value;
            }
        }

        return last;
    }

    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.Number => ParseNumber(value) is not (0 or double.NaN),
        JsonValueKind.String => value.GetString()!.Length > 0,
        _ => true,
    };

    /// <summary>Legacy <c>end.collections !== collections.length</c>: equal only to a JSON number of the same value.</summary>
    private static bool NumberEquals(JsonElement? value, long expected) =>
        value is { ValueKind: JsonValueKind.Number } number && ParseNumber(number) == expected;

    /// <summary>JavaScript <c>Number(value)</c> for the comparison <c>header.version &gt; 1</c> (NaN compares false).</summary>
    private static double JsNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => ParseNumber(value),
        JsonValueKind.True => 1,
        JsonValueKind.False or JsonValueKind.Null => 0,
        JsonValueKind.String => value.GetString()!.Trim() is var text && text.Length == 0 ? 0
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
        _ => double.NaN,
    };

    /// <summary>JSON.parse: out-of-range numbers become ±Infinity.</summary>
    private static double ParseNumber(JsonElement number) =>
        double.Parse(number.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>JSON value → BSON as the legacy driver serializes a JSON.parse result (js-bson 1.x number rule, PLAN.md §4.3).</summary>
    private static BsonValue ToBson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ToBsonDocument(value),
        JsonValueKind.Array => new BsonArray(value.EnumerateArray().Select(ToBson)),
        JsonValueKind.String => new BsonString(value.GetString()!),
        JsonValueKind.Number => ParseNumber(value) is var number && number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue
            ? new BsonInt32((int)number)
            : new BsonDouble(number),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        _ => BsonNull.Value,
    };

    private static BsonDocument ToBsonDocument(JsonElement value)
    {
        var document = new BsonDocument();
        foreach (var property in value.EnumerateObject())
        {
            document[property.Name] = ToBson(property.Value); // a duplicate key keeps its first position and the last value, like JSON.parse
        }

        return document;
    }

    private sealed class ReaderState
    {
        public bool HasHeader { get; set; }

        public JsonElement? CreatedAt { get; set; }

        public JsonElement? Database { get; set; }

        public HashSet<string> CollectionNames { get; } = new(StringComparer.Ordinal);

        public long Documents { get; set; }

        public JsonElement End { get; set; }

        public bool EndIsTruthy { get; set; }
    }
}
