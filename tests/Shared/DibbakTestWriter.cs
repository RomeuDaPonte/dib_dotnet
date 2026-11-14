using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MongoDB.Bson;

namespace Dib4t.Tests.Common;

/// <summary>
/// Builds <c>.dibbak</c> files frame by frame for tests, including broken ones. Mirrors the legacy
/// <c>BackupWriter</c> (<c>helpers/backupFormat.js</c>): magic, frames <c>[type][uint32 LE length][payload]</c>, gzip.
/// Test-only; independent of the app's reader.
/// </summary>
public sealed class DibbakTestWriter
{
    public const byte Header = 0x48;
    public const byte Collection = 0x43;
    public const byte Document = 0x44;
    public const byte End = 0x5A;

    public const string DefaultHeaderJson =
        """{"format":"dib4t-backup","version":1,"createdAt":"2026-11-11T16:15:31.123Z","createdBy":"test@dib4t.local","database":"dib4t","serverVersion":"4.1.4","collections":[]}""";

    private readonly List<byte> _content = [];

    public DibbakTestWriter Magic(string magic = "DIBBAK01")
    {
        _content.AddRange(Encoding.ASCII.GetBytes(magic));
        return this;
    }

    public DibbakTestWriter Frame(byte type, byte[] payload, uint? declaredLength = null)
    {
        var prefix = new byte[5];
        prefix[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1), declaredLength ?? (uint)payload.Length);
        _content.AddRange(prefix);
        _content.AddRange(payload);
        return this;
    }

    public DibbakTestWriter Json(byte type, string json) => Frame(type, Encoding.UTF8.GetBytes(json));

    public DibbakTestWriter HeaderFrame(string json = DefaultHeaderJson) => Json(Header, json);

    public DibbakTestWriter CollectionFrame(string name, string options = "{}", string indexes = """[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.NAME"}]""", int count = 0) =>
        Json(Collection, $$"""{"name":"{{name}}","options":{{options}},"indexes":{{indexes.Replace("NAME", name, StringComparison.Ordinal)}},"count":{{count}}}""");

    public DibbakTestWriter DocumentFrame(BsonDocument document) => Frame(Document, document.ToBson());

    public DibbakTestWriter EndFrame(int collections, long documents) =>
        Json(End, $$"""{"collections":{{collections}},"documents":{{documents}}}""");

    public DibbakTestWriter Raw(byte[] bytes)
    {
        _content.AddRange(bytes);
        return this;
    }

    /// <summary>The decompressed content.</summary>
    public byte[] ToContent() => _content.ToArray();

    /// <summary>The file as the legacy app writes it (single gzip member).</summary>
    public byte[] ToFile() => Gzip(ToContent());

    public static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>A complete, valid backup with the given collections (in this order) and documents.</summary>
    public static DibbakTestWriter Valid(params (string Name, BsonDocument[] Documents)[] collections)
    {
        var writer = new DibbakTestWriter().Magic().HeaderFrame();
        foreach (var (name, documents) in collections)
        {
            writer.CollectionFrame(name, count: documents.Length);
            foreach (var document in documents)
            {
                writer.DocumentFrame(document);
            }
        }

        return writer.EndFrame(collections.Length, collections.Sum(c => c.Documents.Length));
    }
}
