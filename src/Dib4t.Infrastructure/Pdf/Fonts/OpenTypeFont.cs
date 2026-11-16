using System.Buffers.Binary;
using System.Text;

namespace Dib4t.Infrastructure.Pdf.Fonts;

/// <summary>
/// A TrueType/OpenType font read the way fontkit (bundled in the legacy pdfmake) reads it: the tables the legacy
/// layout uses (<c>head</c>, <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>, <c>cmap</c>, <c>GDEF</c>, <c>GSUB</c>, <c>GPOS</c>) and those the
/// PDF needs to embed it (<c>glyf</c>, <c>loca</c>, <c>OS/2</c>, <c>post</c>, <c>name</c>). Immutable; shared by every document.
/// </summary>
internal sealed class OpenTypeFont
{
    private static readonly Lazy<OpenTypeFont> RegularFont = new(() => new OpenTypeFont(PdfAssets.Read(PdfAssets.RobotoRegular)));
    private static readonly Lazy<OpenTypeFont> MediumFont = new(() => new OpenTypeFont(PdfAssets.Read(PdfAssets.RobotoMedium)));

    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly ushort[] _advances;
    private readonly CmapSubtable _cmap;

    public OpenTypeFont(byte[] data)
    {
        Data = data;
        var numTables = U16(4);
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + (i * 16);
            _tables[Encoding.ASCII.GetString(data, record, 4)] = ((int)U32(record + 8), (int)U32(record + 12));
        }

        var head = Table("head");
        UnitsPerEm = U16(head + 18);
        BoundingBox = (I16(head + 36), I16(head + 38), I16(head + 40), I16(head + 42));
        IndexToLocFormat = I16(head + 50);

        var hhea = Table("hhea");
        Ascent = I16(hhea + 4);
        Descent = I16(hhea + 6);
        LineGap = I16(hhea + 8);
        var numberOfHMetrics = U16(hhea + 34);
        NumGlyphs = U16(Table("maxp") + 4);

        var hmtx = Table("hmtx");
        _advances = new ushort[NumGlyphs];
        for (var g = 0; g < NumGlyphs; g++)
        {
            _advances[g] = U16(hmtx + (Math.Min(g, numberOfHMetrics - 1) * 4));
        }

        _cmap = CmapSubtable.Find(this, Table("cmap"));
        PostScriptName = ReadPostScriptName();
        if (_tables.ContainsKey("OS/2"))
        {
            var os2 = Table("OS/2");
            CapHeight = U16(os2) >= 2 ? I16(os2 + 88) : null;
            XHeight = U16(os2) >= 2 ? I16(os2 + 86) : null;
        }

        if (_tables.ContainsKey("post"))
        {
            var post = Table("post");
            ItalicAngle = I32(post + 4) / 65536.0;
            IsFixedPitch = U32(post + 12) != 0;
        }

        Gdef = _tables.TryGetValue("GDEF", out var gdef) ? GlyphDefinitions.Read(this, gdef.Offset) : null;
        Gsub = _tables.TryGetValue("GSUB", out var gsub) ? LayoutTable.Read(this, gsub.Offset, isGpos: false) : null;
        Gpos = _tables.TryGetValue("GPOS", out var gpos) ? LayoutTable.Read(this, gpos.Offset, isGpos: true) : null;
    }

    /// <summary>pdfmake's <c>Roboto-Regular.ttf</c> (the description).</summary>
    public static OpenTypeFont Regular => RegularFont.Value;

    /// <summary>pdfmake's <c>Roboto-Medium.ttf</c> (every <c>bold: true</c> text).</summary>
    public static OpenTypeFont Medium => MediumFont.Value;

    public byte[] Data { get; }

    public int UnitsPerEm { get; }

    public (short XMin, short YMin, short XMax, short YMax) BoundingBox { get; }

    public short IndexToLocFormat { get; }

    /// <summary><c>hhea.ascent</c> (fontkit <c>font.ascent</c>).</summary>
    public short Ascent { get; }

    /// <summary><c>hhea.descent</c> (fontkit <c>font.descent</c>).</summary>
    public short Descent { get; }

    /// <summary><c>hhea.lineGap</c> (fontkit <c>font.lineGap</c>).</summary>
    public short LineGap { get; }

    public int NumGlyphs { get; }

    public string PostScriptName { get; }

    public short? CapHeight { get; }

    public short? XHeight { get; }

    public double ItalicAngle { get; }

    public bool IsFixedPitch { get; }

    public GlyphDefinitions? Gdef { get; }

    public LayoutTable? Gsub { get; }

    public LayoutTable? Gpos { get; }

    /// <summary>The glyph's advance in font units (<c>hmtx</c>; glyphs past <c>numberOfHMetrics</c> take the last one).</summary>
    public int AdvanceWidth(int glyph) => glyph >= 0 && glyph < _advances.Length ? _advances[glyph] : _advances[^1];

    /// <summary>fontkit <c>CmapProcessor.lookup</c>: the glyph of a code point, 0 when the font has none.</summary>
    public int GlyphForCodePoint(int codePoint) => _cmap.Lookup(codePoint);

    /// <summary>The <c>(offset, length)</c> of a table, or throws when the font has none.</summary>
    public (int Offset, int Length) TableRange(string tag) =>
        _tables.TryGetValue(tag, out var range) ? range : throw new InvalidDataException($"Font has no '{tag}' table.");

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    public IEnumerable<string> TableTags => _tables.Keys;

    internal int Table(string tag) => TableRange(tag).Offset;

    internal ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset));

    internal short I16(int offset) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(offset));

    internal uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset));

    internal int I32(int offset) => BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(offset));

    internal ushort[] U16Array(int offset, int count)
    {
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = U16(offset + (i * 2));
        }

        return result;
    }

    private string ReadPostScriptName()
    {
        if (!_tables.TryGetValue("name", out var name))
        {
            return "Font";
        }

        var count = U16(name.Offset + 2);
        var strings = name.Offset + U16(name.Offset + 4);
        for (var i = 0; i < count; i++)
        {
            var record = name.Offset + 6 + (i * 12);
            if (U16(record + 6) != 6)
            {
                continue;
            }

            var length = U16(record + 8);
            var offset = strings + U16(record + 10);
            return U16(record) is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(Data, offset, length)
                : Encoding.Latin1.GetString(Data, offset, length);
        }

        return "Font";
    }

    /// <summary>The Unicode <c>cmap</c> subtable fontkit picks (its <c>findSubtable</c> order).</summary>
    private sealed class CmapSubtable
    {
        private static readonly (int Platform, int Encoding)[] Preference =
            [(3, 10), (0, 6), (0, 4), (3, 1), (0, 3), (0, 2), (0, 1), (0, 0)];

        private readonly OpenTypeFont _font;
        private readonly int _offset;
        private readonly int _format;

        private CmapSubtable(OpenTypeFont font, int offset)
        {
            _font = font;
            _offset = offset;
            _format = font.U16(offset);
            if (_format is not (4 or 12))
            {
                throw new InvalidDataException($"cmap format {_format} is not supported.");
            }
        }

        public static CmapSubtable Find(OpenTypeFont font, int cmap)
        {
            var count = font.U16(cmap + 2);
            foreach (var (platform, encoding) in Preference)
            {
                for (var i = 0; i < count; i++)
                {
                    var record = cmap + 4 + (i * 8);
                    if (font.U16(record) == platform && font.U16(record + 2) == encoding)
                    {
                        return new CmapSubtable(font, cmap + (int)font.U32(record + 4));
                    }
                }
            }

            throw new InvalidDataException("Font has no Unicode cmap.");
        }

        public int Lookup(int codePoint) => _format == 4 ? LookupFormat4(codePoint) : LookupFormat12(codePoint);

        private int LookupFormat4(int codePoint)
        {
            var segCount = _font.U16(_offset + 6) / 2;
            var endCodes = _offset + 14;
            var startCodes = endCodes + (segCount * 2) + 2;
            var idDeltas = startCodes + (segCount * 2);
            var idRangeOffsets = idDeltas + (segCount * 2);
            var glyphIndexArray = idRangeOffsets + (segCount * 2);
            var glyphIndexCount = (_offset + _font.U16(_offset + 2) - glyphIndexArray) / 2;
            int min = 0, max = segCount - 1;
            while (min <= max)
            {
                var mid = (min + max) >> 1;
                if (codePoint < _font.U16(startCodes + (mid * 2)))
                {
                    max = mid - 1;
                }
                else if (codePoint > _font.U16(endCodes + (mid * 2)))
                {
                    min = mid + 1;
                }
                else
                {
                    var rangeOffset = _font.U16(idRangeOffsets + (mid * 2));
                    int gid;
                    if (rangeOffset == 0)
                    {
                        gid = codePoint + _font.I16(idDeltas + (mid * 2));
                    }
                    else
                    {
                        var index = (rangeOffset / 2) + (codePoint - _font.U16(startCodes + (mid * 2))) - (segCount - mid);
                        gid = index >= 0 && index < glyphIndexCount ? _font.U16(glyphIndexArray + (index * 2)) : 0;
                        if (gid != 0)
                        {
                            gid += _font.I16(idDeltas + (mid * 2));
                        }
                    }

                    return gid & 0xffff;
                }
            }

            return 0;
        }

        private int LookupFormat12(int codePoint)
        {
            var groups = (int)_font.U32(_offset + 12);
            int min = 0, max = groups - 1;
            while (min <= max)
            {
                var mid = (min + max) >> 1;
                var group = _offset + 16 + (mid * 12);
                if (codePoint < _font.U32(group))
                {
                    max = mid - 1;
                }
                else if (codePoint > _font.U32(group + 4))
                {
                    min = mid + 1;
                }
                else
                {
                    return (int)(_font.U32(group + 8) + (uint)(codePoint - _font.U32(group)));
                }
            }

            return 0;
        }
    }
}
