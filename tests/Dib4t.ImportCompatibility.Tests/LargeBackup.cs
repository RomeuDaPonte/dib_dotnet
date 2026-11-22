using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MongoDB.Bson;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Writes a synthetic <c>.dibbak</c> of a given size straight to disk (never in memory): one collection <c>tasks</c>
/// of documents with 1 KB of random bytes each, so the gzip file is as large as its content (PLAN.md §11.3: "a 1 GB
/// synthetic backup").
/// </summary>
internal static class LargeBackup
{
    private const int PayloadBytes = 1024;

    /// <returns>The number of documents and the uncompressed size of their content.</returns>
    public static async Task<(long Documents, long Payload)> WriteAsync(string path, long minimumFileBytes, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        long documents = 0;
        long payload = 0;
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync("DIBBAK01"u8.ToArray(), cancellationToken);
            await FrameAsync(gzip, 'H', Encoding.UTF8.GetBytes(
                """{"format":"dib4t-backup","version":1,"createdAt":"2026-11-22T10:00:00.000Z","createdBy":null,"database":"dib4t","serverVersion":"4.1.4","collections":["tasks"]}"""),
                cancellationToken);
            await FrameAsync(gzip, 'C', Encoding.UTF8.GetBytes(
                """{"name":"tasks","options":{},"indexes":[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.tasks"}],"count":0}"""),
                cancellationToken);

            var random = new Random(20261122);
            var bytes = new byte[PayloadBytes];
            while (file.Length < minimumFileBytes)
            {
                random.NextBytes(bytes);
                var document = new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() },
                    { "n", (int)documents },
                    { "data", new BsonBinaryData(bytes) },
                }.ToBson();
                await FrameAsync(gzip, 'D', document, cancellationToken);
                documents++;
                payload += document.Length;
            }

            await FrameAsync(gzip, 'Z', Encoding.UTF8.GetBytes($$"""{"collections":1,"documents":{{documents}}}"""), cancellationToken);
        }

        return (documents, payload);
    }

    private static async Task FrameAsync(Stream output, char type, byte[] payload, CancellationToken cancellationToken)
    {
        var prefix = new byte[5];
        prefix[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1), (uint)payload.Length);
        await output.WriteAsync(prefix, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
    }
}
