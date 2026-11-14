using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Dib4t.Tests.Common;

/// <summary>
/// Minimal, independent reader of the legacy backup format (back-end/helpers/backupFormat.js, PLAN.md §7.6):
/// gzip("DIBBAK01" + frames), frame = [type u8][len u32 LE][payload]; H header, C collection, D raw BSON document,
/// Z end. Test oracle only: it reads the seed backup and the backups downloaded from the legacy API, so the
/// harness does not depend on the app's own reader.
/// </summary>
public sealed class DibbakFile
{
    private static readonly byte[] Magic = "DIBBAK01"u8.ToArray();

    private DibbakFile(JsonElement header, IReadOnlyList<DibbakCollectionFrame> collections)
    {
        Header = header;
        Collections = collections;
    }

    public JsonElement Header { get; }

    public IReadOnlyList<DibbakCollectionFrame> Collections { get; }

    public int DocumentCount => Collections.Sum(c => c.Documents.Count);

    public static DibbakFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static DibbakFile Parse(byte[] gzipped)
    {
        using var input = new MemoryStream(gzipped);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gzip.CopyTo(plain);
        var bytes = plain.ToArray();

        if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a .dibbak file (magic)");
        }

        JsonElement? header = null;
        JsonElement? end = null;
        var collections = new List<DibbakCollectionFrame>();
        var offset = Magic.Length;
        while (offset < bytes.Length)
        {
            var type = (char)bytes[offset];
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
            var payload = bytes.AsSpan(offset + 5, length);
            offset += 5 + length;
            switch (type)
            {
                case 'H':
                    header = JsonDocument.Parse(payload.ToArray()).RootElement.Clone();
                    break;
                case 'C':
                    var meta = JsonDocument.Parse(payload.ToArray()).RootElement.Clone();
                    collections.Add(new DibbakCollectionFrame(meta.GetProperty("name").GetString()!, meta, new List<byte[]>()));
                    break;
                case 'D':
                    ((List<byte[]>)collections[^1].Documents).Add(payload.ToArray());
                    break;
                case 'Z':
                    end = JsonDocument.Parse(payload.ToArray()).RootElement.Clone();
                    break;
                default:
                    throw new InvalidDataException($"unknown frame type 0x{(int)type:x2}");
            }
        }

        if (header is null || end is null)
        {
            throw new InvalidDataException("incomplete .dibbak file (header or end frame missing)");
        }

        var file = new DibbakFile(header.Value, collections);
        if (end.Value.GetProperty("documents").GetInt32() != file.DocumentCount)
        {
            throw new InvalidDataException("document count in the end frame does not match");
        }

        return file;
    }

    public DbSnapshot ToSnapshot() => DbSnapshot.From(
        Collections.ToDictionary(c => c.Name, c => c.Documents.Select(d => BsonSerializer.Deserialize<BsonDocument>(d))));
}

/// <summary>One collection of a backup: its <c>C</c> frame (name, options, indexes, count) and raw BSON documents.</summary>
public sealed record DibbakCollectionFrame(string Name, JsonElement Meta, IReadOnlyList<byte[]> Documents);
