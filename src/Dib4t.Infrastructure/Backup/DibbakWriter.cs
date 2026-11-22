using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// Writes the frames of a <c>.dibbak</c> (legacy <c>BackupWriter</c> in <c>helpers/backupFormat.js</c>, PLAN.md §7.6)
/// to the decompressed stream: the magic, then <c>[type: 1 byte][length: uint32 LE][payload]</c> per frame, each
/// frame in one write.
/// </summary>
internal sealed class DibbakWriter(Stream content)
{
    public ValueTask WriteMagicAsync(CancellationToken cancellationToken) =>
        content.WriteAsync(DibbakFormat.Magic.ToArray(), cancellationToken);

    /// <summary>A JSON frame (legacy <c>writeJsonFrame</c>: <c>JSON.stringify</c> of the value, UTF-8).</summary>
    public ValueTask WriteJsonFrameAsync(byte type, Action<Utf8JsonWriter> write, CancellationToken cancellationToken) =>
        WriteFrameAsync(type, JsJson.ToUtf8(write), cancellationToken);

    public async ValueTask WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = ArrayPool<byte>.Shared.Rent(DibbakFormat.FramePrefixLength + payload.Length);
        try
        {
            WritePrefix(frame, type, payload.Length);
            payload.Span.CopyTo(frame.AsSpan(DibbakFormat.FramePrefixLength));
            await content.WriteAsync(frame.AsMemory(0, DibbakFormat.FramePrefixLength + payload.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>A <c>D</c> frame: the document's raw bytes as the server sent them (no re-serialization, §4.0).</summary>
    public async ValueTask WriteDocumentAsync(RawBsonDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var slice = document.Slice;
        var length = slice.Length;
        var frame = ArrayPool<byte>.Shared.Rent(DibbakFormat.FramePrefixLength + length);
        try
        {
            WritePrefix(frame, DibbakFormat.DocumentFrame, length);
            slice.GetBytes(0, frame, DibbakFormat.FramePrefixLength, length);
            await content.WriteAsync(frame.AsMemory(0, DibbakFormat.FramePrefixLength + length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static void WritePrefix(byte[] frame, byte type, int length)
    {
        frame[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), (uint)length);
    }
}

/// <summary>
/// Passes writes on to the response until <see cref="Cut"/>: after a failure the gzip stream is disposed without its
/// final block and trailer reaching the client (which would make a cut-off backup a well-formed gzip file).
/// </summary>
internal sealed class CuttableStream(Stream inner) : Stream
{
    private bool _cut;

    public void Cut() => _cut = true;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
        if (!_cut)
        {
            inner.Flush();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => _cut ? Task.CompletedTask : inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_cut)
        {
            inner.Write(buffer, offset, count);
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _cut ? ValueTask.CompletedTask : inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
