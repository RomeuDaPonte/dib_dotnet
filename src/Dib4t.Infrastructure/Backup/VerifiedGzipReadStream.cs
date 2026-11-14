using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using Dib4t.Application.Database;

namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// Decompresses a gzip file and, at the end, checks the gzip trailer (CRC32 and size of the data), throwing
/// <see cref="BackupFormatException"/> with the legacy text when it does not match.
/// </summary>
/// <remarks>
/// <see cref="GZipStream"/> returns the data it has when the input is cut off (no error), while the legacy
/// <c>zlib.gunzip</c> fails with "unexpected end of file", which the legacy restore reports as
/// "O ficheiro não é um backup válido da Dib4t." (recorded step <c>db-restore-truncated</c>). Comparing the trailer
/// of the file with the decompressed data restores that behavior. The legacy app and the new app write single-member
/// gzip files with no trailing bytes; a multi-member file or bytes after the gzip member are rejected here.
/// </remarks>
internal sealed class VerifiedGzipReadStream : Stream
{
    private const int TrailerLength = 8;
    private const int MinimumGzipLength = 18; // 10-byte header, empty deflate block, 8-byte trailer

    private readonly Stream _compressed;
    private readonly long _compressedLength;
    private readonly GZipStream _gzip;
    private readonly Crc32 _crc = new();
    private long _length;
    private bool _finished;

    /// <param name="compressed">The gzip file, seekable, positioned at its start.</param>
    public VerifiedGzipReadStream(Stream compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (!compressed.CanSeek)
        {
            throw new ArgumentException("The backup stream must be seekable.", nameof(compressed));
        }

        _compressed = compressed;
        _compressedLength = compressed.Length - compressed.Position;
        _gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_finished || buffer.IsEmpty)
        {
            return 0;
        }

        int read;
        try
        {
            read = _gzip.Read(buffer);
        }
        catch (InvalidDataException)
        {
            throw new BackupFormatException(BackupMessages.NotAValidBackup);
        }

        if (read == 0)
        {
            _finished = true;
            CheckTrailer(ReadTrailer());
            return 0;
        }

        return Advance(buffer[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_finished || buffer.IsEmpty)
        {
            return 0;
        }

        int read;
        try
        {
            read = await _gzip.ReadAsync(buffer, cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new BackupFormatException(BackupMessages.NotAValidBackup);
        }

        if (read == 0)
        {
            _finished = true;
            CheckTrailer(await ReadTrailerAsync(cancellationToken));
            return 0;
        }

        return Advance(buffer.Span[..read]);
    }

    private int Advance(ReadOnlySpan<byte> data)
    {
        _crc.Append(data);
        _length += data.Length;
        return data.Length;
    }

    private byte[] ReadTrailer()
    {
        var trailer = new byte[TrailerLength];
        if (_compressedLength >= MinimumGzipLength)
        {
            _compressed.Seek(-TrailerLength, SeekOrigin.End);
            _compressed.ReadExactly(trailer);
        }

        return trailer;
    }

    private async ValueTask<byte[]> ReadTrailerAsync(CancellationToken cancellationToken)
    {
        var trailer = new byte[TrailerLength];
        if (_compressedLength >= MinimumGzipLength)
        {
            _compressed.Seek(-TrailerLength, SeekOrigin.End);
            await _compressed.ReadExactlyAsync(trailer, cancellationToken);
        }

        return trailer;
    }

    private void CheckTrailer(byte[] trailer)
    {
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4));
        if (_compressedLength < MinimumGzipLength || crc != _crc.GetCurrentHashAsUInt32() || size != unchecked((uint)_length))
        {
            throw new BackupFormatException(BackupMessages.NotAValidBackup);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gzip.Dispose();
        }

        base.Dispose(disposing);
    }
}
