namespace Dib4t.Infrastructure.Backup;

/// <summary>
/// The legacy <c>.dibbak</c> format (<c>helpers/backupFormat.js</c>, PLAN.md §7.6): a gzip stream holding
/// <c>"DIBBAK01"</c> and then frames <c>[type: 1 byte][length: uint32 LE][payload]</c>.
/// </summary>
internal static class DibbakFormat
{
    public const string FormatName = "dib4t-backup";
    public const int FormatVersion = 1;
    public const string FileExtension = ".dibbak";

    /// <summary><c>H</c>: header (JSON), always the first frame.</summary>
    public const byte HeaderFrame = 0x48;

    /// <summary><c>C</c>: collection (JSON <c>{ name, options, indexes, count }</c>), starts a collection.</summary>
    public const byte CollectionFrame = 0x43;

    /// <summary><c>D</c>: one raw BSON document of the current collection.</summary>
    public const byte DocumentFrame = 0x44;

    /// <summary><c>Z</c>: end (JSON <c>{ collections, documents }</c>), always the last frame.</summary>
    public const byte EndFrame = 0x5A;

    public const int FramePrefixLength = 5;

    /// <summary>MongoDB's maximum document size: a larger document frame cannot be inserted (rejected before anything is dropped).</summary>
    public const int MaxDocumentLength = 16 * 1024 * 1024;

    /// <summary>Upper bound for a JSON frame kept in memory (real header/collection frames are a few KB).</summary>
    public const int MaxJsonFrameLength = 64 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "DIBBAK01"u8;
}
