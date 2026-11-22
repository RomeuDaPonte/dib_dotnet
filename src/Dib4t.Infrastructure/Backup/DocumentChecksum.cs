using System.IO.Hashing;
using MongoDB.Bson.IO;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// Order-independent checksum of a set of raw BSON documents (the built-in verification, PLAN.md §2.2 step 3): the
/// wrapping sum of each document's XxHash128. Any change to a document's bytes (a field name, its order, a BSON type
/// or a value) changes it; the order the server returns the documents in does not. Constant memory for any size.
/// </summary>
internal sealed class DocumentChecksum
{
    public long Count { get; private set; }

    public UInt128 Value { get; private set; }

    public void Add(ReadOnlySpan<byte> document)
    {
        Value += XxHash128.HashToUInt128(document);
        Count++;
    }

    public void Add(IByteBuffer document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segment = document.AccessBackingBytes(0);
        if (segment.Count >= document.Length)
        {
            Add(segment.AsSpan(0, document.Length));
            return;
        }

        var bytes = new byte[document.Length];
        document.GetBytes(0, bytes, 0, bytes.Length);
        Add(bytes);
    }
}
