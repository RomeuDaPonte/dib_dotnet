namespace Dib4t.Infrastructure.Pdf.Fonts;

// The OpenType layout tables (GSUB, GPOS, GDEF) as fontkit's table definitions expose them, so the processors
// below can be ported statement for statement (array orders as stored in the font, e.g. backtrack sequences).

/// <summary>A coverage table (fontkit <c>coverageIndex</c>: format 1 is searched with <c>indexOf</c>).</summary>
internal sealed class Coverage(ushort[]? glyphs, (int Start, int End, int StartIndex)[]? ranges)
{
    public int IndexOf(int glyph)
    {
        if (glyphs is not null)
        {
            return glyph is >= 0 and <= 0xffff ? Array.IndexOf(glyphs, (ushort)glyph) : -1;
        }

        foreach (var (start, end, startIndex) in ranges!)
        {
            if (start <= glyph && glyph <= end)
            {
                return startIndex + glyph - start;
            }
        }

        return -1;
    }

    public static Coverage Read(OpenTypeFont f, int at)
    {
        var format = f.U16(at);
        var count = f.U16(at + 2);
        if (format == 1)
        {
            return new Coverage(f.U16Array(at + 4, count), null);
        }

        var ranges = new (int, int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var r = at + 4 + (i * 6);
            ranges[i] = (f.U16(r), f.U16(r + 2), f.U16(r + 4));
        }

        return new Coverage(null, ranges);
    }
}

/// <summary>A class definition table (fontkit <c>getClassID</c>: 0 when the glyph is not listed).</summary>
internal sealed class ClassDefinition(int startGlyph, ushort[]? values, (int Start, int End, int Class)[]? ranges)
{
    public int ClassOf(int glyph)
    {
        if (values is not null)
        {
            var i = glyph - startGlyph;
            return i >= 0 && i < values.Length ? values[i] : 0;
        }

        foreach (var (start, end, cls) in ranges!)
        {
            if (start <= glyph && glyph <= end)
            {
                return cls;
            }
        }

        return 0;
    }

    public static ClassDefinition? ReadOptional(OpenTypeFont f, int at) => at == 0 ? null : Read(f, at);

    public static ClassDefinition Read(OpenTypeFont f, int at)
    {
        var format = f.U16(at);
        if (format == 1)
        {
            return new ClassDefinition(f.U16(at + 2), f.U16Array(at + 6, f.U16(at + 4)), null);
        }

        var count = f.U16(at + 2);
        var ranges = new (int, int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var r = at + 4 + (i * 6);
            ranges[i] = (f.U16(r), f.U16(r + 2), f.U16(r + 4));
        }

        return new ClassDefinition(0, null, ranges);
    }
}

/// <summary>GDEF: the glyph classes (base 1, ligature 2, mark 3) and mark attachment classes.</summary>
internal sealed record GlyphDefinitions(ClassDefinition? GlyphClasses, ClassDefinition? MarkAttachClasses)
{
    public static GlyphDefinitions Read(OpenTypeFont f, int at)
    {
        var glyphClasses = f.U16(at + 4);
        var markAttach = f.U16(at + 10);
        return new GlyphDefinitions(
            glyphClasses == 0 ? null : ClassDefinition.Read(f, at + glyphClasses),
            markAttach == 0 ? null : ClassDefinition.Read(f, at + markAttach));
    }
}

/// <summary>fontkit's lookup flags: <c>{ markAttachmentType, flags: { rightToLeft, ignoreBaseGlyphs, … } }</c>.</summary>
internal sealed record LookupFlags(int MarkAttachmentType, bool IgnoreBaseGlyphs, bool IgnoreLigatures, bool IgnoreMarks)
{
    public static readonly LookupFlags None = new(0, false, false, false);
}

internal sealed record Lookup(int LookupType, LookupFlags Flags, IReadOnlyList<object> SubTables);

internal sealed record LangSys(IReadOnlyList<int> FeatureIndexes);

internal sealed record ScriptTable(LangSys? DefaultLangSys, IReadOnlyList<(string Tag, LangSys LangSys)> LangSysRecords);

internal sealed record FeatureRecord(string Tag, IReadOnlyList<int> LookupListIndexes);

/// <summary>A GSUB or GPOS table: script list, feature list, lookup list.</summary>
internal sealed class LayoutTable
{
    private LayoutTable(IReadOnlyList<(string Tag, ScriptTable Script)> scripts, IReadOnlyList<FeatureRecord> features, IReadOnlyList<Lookup> lookups)
    {
        Scripts = scripts;
        Features = features;
        Lookups = lookups;
    }

    public IReadOnlyList<(string Tag, ScriptTable Script)> Scripts { get; }

    public IReadOnlyList<FeatureRecord> Features { get; }

    public IReadOnlyList<Lookup> Lookups { get; }

    public static LayoutTable Read(OpenTypeFont f, int at, bool isGpos)
    {
        var scriptList = at + f.U16(at + 4);
        var featureList = at + f.U16(at + 6);
        var lookupList = at + f.U16(at + 8);

        var scripts = new List<(string, ScriptTable)>();
        for (var i = 0; i < f.U16(scriptList); i++)
        {
            var record = scriptList + 2 + (i * 6);
            var script = scriptList + f.U16(record + 4);
            var defaultLangSys = f.U16(script);
            var langSys = new List<(string, LangSys)>();
            for (var j = 0; j < f.U16(script + 2); j++)
            {
                var lr = script + 4 + (j * 6);
                langSys.Add((Tag(f, lr), ReadLangSys(f, script + f.U16(lr + 4))));
            }

            scripts.Add((Tag(f, record), new ScriptTable(defaultLangSys == 0 ? null : ReadLangSys(f, script + defaultLangSys), langSys)));
        }

        var features = new List<FeatureRecord>();
        for (var i = 0; i < f.U16(featureList); i++)
        {
            var record = featureList + 2 + (i * 6);
            var feature = featureList + f.U16(record + 4);
            features.Add(new FeatureRecord(Tag(f, record), f.U16Array(feature + 4, f.U16(feature + 2)).Select(x => (int)x).ToArray()));
        }

        var lookups = new List<Lookup>();
        for (var i = 0; i < f.U16(lookupList); i++)
        {
            var lookup = lookupList + f.U16(lookupList + 2 + (i * 2));
            var type = f.U16(lookup);
            var flag = f.U16(lookup + 2);
            var flags = new LookupFlags(flag >> 8, (flag & 0x2) != 0, (flag & 0x4) != 0, (flag & 0x8) != 0);
            var subTables = new List<object>();
            for (var s = 0; s < f.U16(lookup + 4); s++)
            {
                var sub = lookup + f.U16(lookup + 6 + (s * 2));
                subTables.Add(isGpos ? GposSubtables.Read(f, type, sub) : GsubSubtables.Read(f, type, sub));
            }

            lookups.Add(new Lookup(type, flags, subTables));
        }

        return new LayoutTable(scripts, features, lookups);
    }

    private static LangSys ReadLangSys(OpenTypeFont f, int at) => new(f.U16Array(at + 6, f.U16(at + 4)).Select(x => (int)x).ToArray());

    private static string Tag(OpenTypeFont f, int at) => System.Text.Encoding.ASCII.GetString(f.Data, at, 4);
}

/// <summary>A lookup record of a (chaining) context rule.</summary>
internal readonly record struct SequenceLookup(int SequenceIndex, int LookupListIndex);

internal sealed record ContextRule(ushort[] Input, SequenceLookup[] LookupRecords);

internal sealed record ChainRule(ushort[] Backtrack, ushort[] Input, ushort[] Lookahead, SequenceLookup[] LookupRecords);

internal sealed record Context1(Coverage Coverage, ContextRule[][] RuleSets);

internal sealed record Context2(Coverage Coverage, ClassDefinition ClassDef, ContextRule[]?[] ClassSets);

internal sealed record Context3(Coverage[] Coverages, SequenceLookup[] LookupRecords);

internal sealed record Chain1(Coverage Coverage, ChainRule[][] ChainRuleSets);

internal sealed record Chain2(Coverage Coverage, ClassDefinition BacktrackClassDef, ClassDefinition InputClassDef, ClassDefinition LookaheadClassDef, ChainRule[]?[] ChainClassSets);

internal sealed record Chain3(Coverage[] BacktrackCoverage, Coverage[] InputCoverage, Coverage[] LookaheadCoverage, SequenceLookup[] LookupRecords);

/// <summary>Extension subtables (GSUB 7, GPOS 9): the real lookup type and subtable.</summary>
internal sealed record Extension(int LookupType, object SubTable);

/// <summary>A subtable type the legacy fonts do not use; applying it matches nothing.</summary>
internal sealed record UnsupportedSubtable(int LookupType, int Format);

/// <summary>Context and chaining context subtables, shared by GSUB (5, 6) and GPOS (7, 8).</summary>
internal static class ContextSubtables
{
    public static object ReadContext(OpenTypeFont f, int at)
    {
        var format = f.U16(at);
        switch (format)
        {
            case 1:
                {
                    var coverage = Coverage.Read(f, at + f.U16(at + 2));
                    var sets = new ContextRule[f.U16(at + 4)][];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        sets[i] = ReadRules(f, at + f.U16(at + 6 + (i * 2)));
                    }

                    return new Context1(coverage, sets);
                }

            case 2:
                {
                    var coverage = Coverage.Read(f, at + f.U16(at + 2));
                    var classDef = ClassDefinition.Read(f, at + f.U16(at + 4));
                    var sets = new ContextRule[]?[f.U16(at + 6)];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        var offset = f.U16(at + 8 + (i * 2));
                        sets[i] = offset == 0 ? null : ReadRules(f, at + offset);
                    }

                    return new Context2(coverage, classDef, sets);
                }

            case 3:
                {
                    var glyphCount = f.U16(at + 2);
                    var lookupCount = f.U16(at + 4);
                    var coverages = new Coverage[glyphCount];
                    for (var i = 0; i < glyphCount; i++)
                    {
                        coverages[i] = Coverage.Read(f, at + f.U16(at + 6 + (i * 2)));
                    }

                    return new Context3(coverages, ReadLookupRecords(f, at + 6 + (glyphCount * 2), lookupCount));
                }

            default:
                return new UnsupportedSubtable(5, format);
        }
    }

    public static object ReadChaining(OpenTypeFont f, int at)
    {
        var format = f.U16(at);
        switch (format)
        {
            case 1:
                {
                    var coverage = Coverage.Read(f, at + f.U16(at + 2));
                    var sets = new ChainRule[f.U16(at + 4)][];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        sets[i] = ReadChainRules(f, at + f.U16(at + 6 + (i * 2)));
                    }

                    return new Chain1(coverage, sets);
                }

            case 2:
                {
                    var coverage = Coverage.Read(f, at + f.U16(at + 2));
                    var backtrack = ClassDefinition.Read(f, at + f.U16(at + 4));
                    var input = ClassDefinition.Read(f, at + f.U16(at + 6));
                    var lookahead = ClassDefinition.Read(f, at + f.U16(at + 8));
                    var sets = new ChainRule[]?[f.U16(at + 10)];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        var offset = f.U16(at + 12 + (i * 2));
                        sets[i] = offset == 0 ? null : ReadChainRules(f, at + offset);
                    }

                    return new Chain2(coverage, backtrack, input, lookahead, sets);
                }

            case 3:
                {
                    var p = at + 2;
                    var backtrack = ReadCoverages(f, at, ref p);
                    var input = ReadCoverages(f, at, ref p);
                    var lookahead = ReadCoverages(f, at, ref p);
                    var lookupCount = f.U16(p);
                    return new Chain3(backtrack, input, lookahead, ReadLookupRecords(f, p + 2, lookupCount));
                }

            default:
                return new UnsupportedSubtable(6, format);
        }
    }

    private static Coverage[] ReadCoverages(OpenTypeFont f, int table, ref int p)
    {
        var count = f.U16(p);
        var result = new Coverage[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Coverage.Read(f, table + f.U16(p + 2 + (i * 2)));
        }

        p += 2 + (count * 2);
        return result;
    }

    private static ContextRule[] ReadRules(OpenTypeFont f, int set)
    {
        var rules = new ContextRule[f.U16(set)];
        for (var i = 0; i < rules.Length; i++)
        {
            var rule = set + f.U16(set + 2 + (i * 2));
            var glyphCount = f.U16(rule);
            var lookupCount = f.U16(rule + 2);
            var input = f.U16Array(rule + 4, Math.Max(0, glyphCount - 1));
            rules[i] = new ContextRule(input, ReadLookupRecords(f, rule + 4 + (input.Length * 2), lookupCount));
        }

        return rules;
    }

    private static ChainRule[] ReadChainRules(OpenTypeFont f, int set)
    {
        var rules = new ChainRule[f.U16(set)];
        for (var i = 0; i < rules.Length; i++)
        {
            var p = set + f.U16(set + 2 + (i * 2));
            var backtrack = f.U16Array(p + 2, f.U16(p));
            p += 2 + (backtrack.Length * 2);
            var input = f.U16Array(p + 2, Math.Max(0, f.U16(p) - 1));
            p += 2 + (input.Length * 2);
            var lookahead = f.U16Array(p + 2, f.U16(p));
            p += 2 + (lookahead.Length * 2);
            rules[i] = new ChainRule(backtrack, input, lookahead, ReadLookupRecords(f, p + 2, f.U16(p)));
        }

        return rules;
    }

    private static SequenceLookup[] ReadLookupRecords(OpenTypeFont f, int at, int count)
    {
        var records = new SequenceLookup[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = new SequenceLookup(f.U16(at + (i * 4)), f.U16(at + (i * 4) + 2));
        }

        return records;
    }
}

internal sealed record SingleSubst1(Coverage Coverage, int DeltaGlyphId);

internal sealed record SingleSubst2(Coverage Coverage, ushort[] Substitutes);

internal sealed record MultipleSubst(Coverage Coverage, ushort[][] Sequences);

internal sealed record AlternateSubst(Coverage Coverage, ushort[][] AlternateSets);

internal sealed record Ligature(int Glyph, ushort[] Components);

internal sealed record LigatureSubst(Coverage Coverage, Ligature[][] LigatureSets);

internal static class GsubSubtables
{
    public static object Read(OpenTypeFont f, int type, int at)
    {
        var format = f.U16(at);
        switch (type)
        {
            case 1 when format == 1:
                return new SingleSubst1(Coverage.Read(f, at + f.U16(at + 2)), f.I16(at + 4));
            case 1 when format == 2:
                return new SingleSubst2(Coverage.Read(f, at + f.U16(at + 2)), f.U16Array(at + 6, f.U16(at + 4)));
            case 2:
                return new MultipleSubst(Coverage.Read(f, at + f.U16(at + 2)), Sequences(f, at, f.U16(at + 4)));
            case 3:
                return new AlternateSubst(Coverage.Read(f, at + f.U16(at + 2)), Sequences(f, at, f.U16(at + 4)));
            case 4:
                {
                    var sets = new Ligature[f.U16(at + 4)][];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        var set = at + f.U16(at + 6 + (i * 2));
                        var ligatures = new Ligature[f.U16(set)];
                        for (var j = 0; j < ligatures.Length; j++)
                        {
                            var lig = set + f.U16(set + 2 + (j * 2));
                            ligatures[j] = new Ligature(f.U16(lig), f.U16Array(lig + 4, Math.Max(0, f.U16(lig + 2) - 1)));
                        }

                        sets[i] = ligatures;
                    }

                    return new LigatureSubst(Coverage.Read(f, at + f.U16(at + 2)), sets);
                }

            case 5:
                return ContextSubtables.ReadContext(f, at);
            case 6:
                return ContextSubtables.ReadChaining(f, at);
            case 7:
                {
                    var realType = f.U16(at + 2);
                    return new Extension(realType, Read(f, realType, at + (int)f.U32(at + 4)));
                }

            default:
                return new UnsupportedSubtable(type, format);
        }
    }

    private static ushort[][] Sequences(OpenTypeFont f, int at, int count)
    {
        var result = new ushort[count][];
        for (var i = 0; i < count; i++)
        {
            var seq = at + f.U16(at + 6 + (i * 2));
            result[i] = f.U16Array(seq + 2, f.U16(seq));
        }

        return result;
    }
}

/// <summary>A GPOS value record; a field the value format leaves out is <c>null</c> (fontkit applies only present ones).</summary>
internal sealed record ValueRecord(int? XPlacement, int? YPlacement, int? XAdvance, int? YAdvance);

internal sealed record SinglePos1(Coverage Coverage, ValueRecord Value);

internal sealed record SinglePos2(Coverage Coverage, ValueRecord[] Values);

internal sealed record PairValue(int SecondGlyph, ValueRecord Value1, ValueRecord Value2);

internal sealed record PairPos1(Coverage Coverage, PairValue[][] PairSets);

internal sealed record PairPos2(Coverage Coverage, ClassDefinition ClassDef1, ClassDefinition ClassDef2, (ValueRecord Value1, ValueRecord Value2)[][] ClassRecords);

internal static class GposSubtables
{
    public static object Read(OpenTypeFont f, int type, int at)
    {
        var format = f.U16(at);
        switch (type)
        {
            case 1 when format == 1:
                {
                    var p = at + 6;
                    return new SinglePos1(Coverage.Read(f, at + f.U16(at + 2)), ReadValue(f, f.U16(at + 4), ref p));
                }

            case 1 when format == 2:
                {
                    var valueFormat = f.U16(at + 4);
                    var values = new ValueRecord[f.U16(at + 6)];
                    var p = at + 8;
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = ReadValue(f, valueFormat, ref p);
                    }

                    return new SinglePos2(Coverage.Read(f, at + f.U16(at + 2)), values);
                }

            case 2 when format == 1:
                {
                    var vf1 = f.U16(at + 4);
                    var vf2 = f.U16(at + 6);
                    var sets = new PairValue[f.U16(at + 8)][];
                    for (var i = 0; i < sets.Length; i++)
                    {
                        var set = at + f.U16(at + 10 + (i * 2));
                        var pairs = new PairValue[f.U16(set)];
                        var p = set + 2;
                        for (var j = 0; j < pairs.Length; j++)
                        {
                            var second = f.U16(p);
                            p += 2;
                            var v1 = ReadValue(f, vf1, ref p);
                            var v2 = ReadValue(f, vf2, ref p);
                            pairs[j] = new PairValue(second, v1, v2);
                        }

                        sets[i] = pairs;
                    }

                    return new PairPos1(Coverage.Read(f, at + f.U16(at + 2)), sets);
                }

            case 2 when format == 2:
                {
                    var vf1 = f.U16(at + 4);
                    var vf2 = f.U16(at + 6);
                    var class1Count = f.U16(at + 12);
                    var class2Count = f.U16(at + 14);
                    var records = new (ValueRecord, ValueRecord)[class1Count][];
                    var p = at + 16;
                    for (var c1 = 0; c1 < class1Count; c1++)
                    {
                        records[c1] = new (ValueRecord, ValueRecord)[class2Count];
                        for (var c2 = 0; c2 < class2Count; c2++)
                        {
                            var v1 = ReadValue(f, vf1, ref p);
                            var v2 = ReadValue(f, vf2, ref p);
                            records[c1][c2] = (v1, v2);
                        }
                    }

                    return new PairPos2(Coverage.Read(f, at + f.U16(at + 2)), ClassDefinition.Read(f, at + f.U16(at + 8)),
                        ClassDefinition.Read(f, at + f.U16(at + 10)), records);
                }

            case 7:
                return ContextSubtables.ReadContext(f, at);
            case 8:
                return ContextSubtables.ReadChaining(f, at);
            case 9:
                {
                    var realType = f.U16(at + 2);
                    return new Extension(realType, Read(f, realType, at + (int)f.U32(at + 4)));
                }

            default:
                return new UnsupportedSubtable(type, format);
        }
    }

    private static ValueRecord ReadValue(OpenTypeFont f, int format, ref int p)
    {
        int? Next(int bit, ref int pos)
        {
            if ((format & bit) == 0)
            {
                return null;
            }

            var v = f.I16(pos);
            pos += 2;
            return v;
        }

        var xPlacement = Next(0x1, ref p);
        var yPlacement = Next(0x2, ref p);
        var xAdvance = Next(0x4, ref p);
        var yAdvance = Next(0x8, ref p);
        for (var bit = 0x10; bit <= 0x80; bit <<= 1)
        {
            if ((format & bit) != 0)
            {
                p += 2; // device table offsets: fontkit applies them only with font variations
            }
        }

        return new ValueRecord(xPlacement, yPlacement, xAdvance, yAdvance);
    }
}
