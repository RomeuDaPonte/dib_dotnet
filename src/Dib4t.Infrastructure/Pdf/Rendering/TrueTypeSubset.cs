using System.Buffers.Binary;
using System.Text;
using Dib4t.Infrastructure.Pdf.Fonts;

namespace Dib4t.Infrastructure.Pdf.Rendering;

/// <summary>
/// A TrueType font program for <c>FontFile2</c> with only the used glyphs' outlines (and the components of composite
/// glyphs). Glyph ids are kept, so the PDF maps CIDs to glyphs with <c>/CIDToGIDMap /Identity</c>.
/// </summary>
internal static class TrueTypeSubset
{
    private static readonly string[] Kept = ["cvt ", "fpgm", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "prep"];

    public static byte[] Create(OpenTypeFont font, IEnumerable<int> usedGlyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        var glyphs = Closure(font, usedGlyphs);
        var (glyf, loca) = Outlines(font, glyphs);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in Kept.Where(font.HasTable))
        {
            tables[tag] = tag switch
            {
                "glyf" => glyf,
                "loca" => loca,
                _ => Slice(font, tag),
            };
        }

        BinaryPrimitives.WriteInt16BigEndian(tables["head"].AsSpan(50), 1); // indexToLocFormat: long offsets
        BinaryPrimitives.WriteUInt32BigEndian(tables["head"].AsSpan(8), 0); // checkSumAdjustment, set below
        var file = Assemble(tables);
        var headOffset = TableOffset(file, "head");
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(file)));
        return file;
    }

    /// <summary>The used glyphs, <c>.notdef</c> and every component of a composite glyph.</summary>
    private static SortedSet<int> Closure(OpenTypeFont font, IEnumerable<int> usedGlyphs)
    {
        var result = new SortedSet<int>();
        var pending = new Stack<int>(usedGlyphs.Where(g => g >= 0 && g < font.NumGlyphs).Append(0));
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (!result.Add(glyph))
            {
                continue;
            }

            var (offset, length) = GlyphRange(font, glyph);
            if (length < 10 || font.I16(offset) >= 0)
            {
                continue; // empty or simple glyph
            }

            var p = offset + 10;
            while (true)
            {
                var flags = font.U16(p);
                pending.Push(font.U16(p + 2));
                p += 4 + ((flags & 0x1) != 0 ? 4 : 2);
                p += (flags & 0x8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                if ((flags & 0x20) == 0)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static (byte[] Glyf, byte[] Loca) Outlines(OpenTypeFont font, SortedSet<int> glyphs)
    {
        using var glyf = new MemoryStream();
        var loca = new byte[(font.NumGlyphs + 1) * 4];
        for (var g = 0; g < font.NumGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            if (!glyphs.Contains(g))
            {
                continue;
            }

            var (offset, length) = GlyphRange(font, g);
            glyf.Write(font.Data, offset, length);
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(font.NumGlyphs * 4), (uint)glyf.Length);
        return (glyf.ToArray(), loca);
    }

    private static (int Offset, int Length) GlyphRange(OpenTypeFont font, int glyph)
    {
        var loca = font.Table("loca");
        var glyfTable = font.Table("glyf");
        int start, end;
        if (font.IndexToLocFormat == 0)
        {
            start = font.U16(loca + (glyph * 2)) * 2;
            end = font.U16(loca + ((glyph + 1) * 2)) * 2;
        }
        else
        {
            start = (int)font.U32(loca + (glyph * 4));
            end = (int)font.U32(loca + ((glyph + 1) * 4));
        }

        return (glyfTable + start, Math.Max(0, end - start));
    }

    private static byte[] Slice(OpenTypeFont font, string tag)
    {
        var (offset, length) = font.TableRange(tag);
        return font.Data.AsSpan(offset, length).ToArray();
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var numTables = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(numTables));
        var searchRange = (1 << entrySelector) * 16;
        using var file = new MemoryStream();
        var header = new byte[12 + (numTables * 16)];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((numTables * 16) - searchRange));
        file.Write(header);
        var i = 0;
        foreach (var (tag, data) in tables)
        {
            var record = 12 + (i++ * 16);
            Encoding.ASCII.GetBytes(tag, header.AsSpan(record));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 8), (uint)file.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 12), (uint)data.Length);
            file.Write(data);
            while (file.Length % 4 != 0)
            {
                file.WriteByte(0);
            }
        }

        var result = file.ToArray();
        header.CopyTo(result, 0);
        return result;
    }

    private static int TableOffset(byte[] file, string tag)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(file, record, 4) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(record + 8));
            }
        }

        throw new InvalidDataException($"No '{tag}' table.");
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : (byte)0);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }
}
