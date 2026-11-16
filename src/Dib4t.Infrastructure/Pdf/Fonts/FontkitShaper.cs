using Dib4t.Infrastructure.Pdf.Text;

namespace Dib4t.Infrastructure.Pdf.Fonts;

// Port of the OpenType shaping of fontkit 1.x as bundled in the legacy pdfmake 0.1.37 (LayoutEngine, OTLayoutEngine,
// DefaultShaper, ShapingPlan, GlyphInfo, GlyphIterator, OTProcessor, GSUBProcessor, GPOSProcessor), statement for
// statement and with its quirks, for the scripts and lookup types of pdfmake's Roboto fonts: every script those fonts
// list (DFLT, latn, grek, cyrl) is shaped by fontkit's DefaultShaper. Checked glyph for glyph against fontkit in
// shaping.json (tests/fixtures/legacy/pdf-layout).

/// <summary>fontkit <c>GlyphPosition</c>, in font units (scaled to 1000 per em by <see cref="LayoutFont"/>).</summary>
internal sealed class GlyphPosition(double xAdvance)
{
    public double XAdvance { get; set; } = xAdvance;

    public double YAdvance { get; set; }

    public double XOffset { get; set; }

    public double YOffset { get; set; }
}

/// <summary>fontkit <c>GlyphInfo</c>: a glyph during substitution and positioning.</summary>
internal sealed class GlyphInfo
{
    private readonly OpenTypeFont _font;
    private int _id;

    public GlyphInfo(OpenTypeFont font, int id, int[]? codePoints, IEnumerable<string>? features)
    {
        _font = font;
        CodePoints = codePoints ?? [];
        Id = id;
        Features = features is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(features, StringComparer.Ordinal);
    }

    public int[] CodePoints { get; }

    /// <summary>The features applied to this glyph (JavaScript <c>feature in glyph.features</c>).</summary>
    public HashSet<string> Features { get; }

    public int Id
    {
        get => _id;
        set
        {
            _id = value;
            var gdef = _font.Gdef;
            if (gdef?.GlyphClasses is { } classes)
            {
                var classId = classes.ClassOf(value);
                IsBase = classId == 1;
                IsLigature = classId == 2;
                IsMark = classId == 3;
                MarkAttachmentType = gdef.MarkAttachClasses?.ClassOf(value) ?? 0;
            }
            else
            {
                // fontkit: codePoints.every(isMark), so a glyph without code points (.notdef) counts as a mark.
                IsMark = CodePoints.All(UnicodeData.IsMark);
                IsBase = !IsMark;
                IsLigature = CodePoints.Length > 1;
                MarkAttachmentType = 0;
            }
        }
    }

    public bool IsBase { get; private set; }

    public bool IsLigature { get; private set; }

    public bool IsMark { get; private set; }

    public int MarkAttachmentType { get; private set; }

    public int? LigatureId { get; set; }

    public int? LigatureComponent { get; set; }

    public bool IsLigated { get; set; }
}

/// <summary>fontkit <c>GlyphIterator</c>.</summary>
internal sealed class GlyphIterator(List<GlyphInfo> glyphs)
{
    public LookupFlags Options { get; private set; } = LookupFlags.None;

    public int Index { get; set; }

    public GlyphInfo? Cur => Index >= 0 && Index < glyphs.Count ? glyphs[Index] : null;

    public void Reset(LookupFlags options, int index = 0)
    {
        Options = options;
        Index = index;
    }

    public GlyphInfo? Next() => Move(+1);

    public GlyphInfo? Peek(int count = 1)
    {
        var idx = Index;
        var res = Increment(count);
        Index = idx;
        return res;
    }

    public int PeekIndex(int count = 1)
    {
        var idx = Index;
        Increment(count);
        var res = Index;
        Index = idx;
        return res;
    }

    public GlyphInfo? Increment(int count = 1)
    {
        var dir = count < 0 ? -1 : 1;
        count = Math.Abs(count);
        while (count-- > 0)
        {
            Move(dir);
        }

        return Cur;
    }

    private bool ShouldIgnore(GlyphInfo glyph) =>
        (Options.IgnoreMarks && glyph.IsMark) || (Options.IgnoreBaseGlyphs && glyph.IsBase) || (Options.IgnoreLigatures && glyph.IsLigature)
        || (Options.MarkAttachmentType != 0 && glyph.IsMark && glyph.MarkAttachmentType != Options.MarkAttachmentType);

    private GlyphInfo? Move(int dir)
    {
        Index += dir;
        while (Index >= 0 && Index < glyphs.Count && ShouldIgnore(glyphs[Index]))
        {
            Index += dir;
        }

        return Index < 0 || Index >= glyphs.Count ? null : glyphs[Index];
    }
}

/// <summary>fontkit <c>OTProcessor</c>: script selection, feature lookups, (chaining) context matching.</summary>
internal abstract class OtProcessor
{
    private static readonly string[] DefaultScripts = ["DFLT", "dflt", "latn"];

    private ScriptTable? _script;
    private string? _scriptTag;
    private Dictionary<string, FeatureRecord> _features = new(StringComparer.Ordinal);

    protected OtProcessor(OpenTypeFont font, LayoutTable table)
    {
        Font = font;
        Table = table;
        SelectScript(null, null);
    }

    protected OpenTypeFont Font { get; }

    protected LayoutTable Table { get; }

    protected List<GlyphInfo> Glyphs { get; private set; } = [];

    protected List<GlyphPosition>? Positions { get; private set; }

    protected GlyphIterator Iterator { get; private set; } = new([]);

    protected string? CurrentFeature { get; private set; }

    protected int NextLigatureId { get; set; } = 1;

    public string? Direction { get; private set; }

    /// <summary><c>selectScript(script, undefined, direction)</c>: the script tag used (the font's own).</summary>
    public string? SelectScript(IReadOnlyList<string>? script, string? direction)
    {
        var entry = FindScript(script) ?? FindScript(DefaultScripts);
        if (entry is null)
        {
            return _scriptTag;
        }

        _scriptTag = entry.Value.Tag;
        _script = entry.Value.Script;
        if (direction is null || direction != Direction)
        {
            Direction = direction ?? Scripts.DirectionOf(script);
        }

        // No language is ever requested: the script's default language system.
        _features = new Dictionary<string, FeatureRecord>(StringComparer.Ordinal);
        if (_script.DefaultLangSys is { } langSys)
        {
            foreach (var featureIndex in langSys.FeatureIndexes)
            {
                var record = Table.Features[featureIndex];
                _features[record.Tag] = record;
            }
        }

        return _scriptTag;
    }

    public void ApplyFeatures(IReadOnlyList<string> userFeatures, List<GlyphInfo> glyphs, List<GlyphPosition>? positions)
    {
        var lookups = new List<(string Feature, int Index, Lookup Lookup)>();
        foreach (var tag in userFeatures)
        {
            if (!_features.TryGetValue(tag, out var feature))
            {
                continue;
            }

            foreach (var lookupIndex in feature.LookupListIndexes)
            {
                lookups.Add((tag, lookupIndex, Table.Lookups[lookupIndex]));
            }
        }

        // Array#sort in Node 16 is stable, as OrderBy is.
        ApplyLookups([.. lookups.OrderBy(l => l.Index)], glyphs, positions);
    }

    protected abstract bool ApplyLookup(int lookupType, object table);

    protected bool ApplyLookupList(IReadOnlyList<SequenceLookup> lookupRecords)
    {
        var options = Iterator.Options;
        var glyphIndex = Iterator.Index;
        foreach (var record in lookupRecords)
        {
            Iterator.Reset(options, glyphIndex);
            Iterator.Increment(record.SequenceIndex);
            var lookup = Table.Lookups[record.LookupListIndex];
            Iterator.Reset(lookup.Flags, Iterator.Index);
            foreach (var table in lookup.SubTables)
            {
                if (ApplyLookup(lookup.LookupType, table))
                {
                    break;
                }
            }
        }

        Iterator.Reset(options, glyphIndex);
        return true;
    }

    protected int CoverageIndex(Coverage coverage, int? glyph = null) => coverage.IndexOf(glyph ?? Iterator.Cur!.Id);

    /// <summary><c>match</c>: walks <paramref name="count"/> items from <paramref name="sequenceIndex"/>; the matched glyph indexes.</summary>
    protected List<int>? Match(int sequenceIndex, int count, Func<int, GlyphInfo, bool> matches)
    {
        var pos = Iterator.Index;
        var glyph = Iterator.Increment(sequenceIndex);
        var idx = 0;
        var matched = new List<int>();
        while (idx < count && glyph is not null && matches(idx, glyph))
        {
            matched.Add(Iterator.Index);
            idx++;
            glyph = Iterator.Next();
        }

        Iterator.Index = pos;
        return idx < count ? null : matched;
    }

    protected bool SequenceMatches(int sequenceIndex, ushort[] sequence) =>
        Match(sequenceIndex, sequence.Length, (i, glyph) => sequence[i] == glyph.Id) is not null;

    protected List<int>? SequenceMatchIndices(int sequenceIndex, ushort[] sequence) =>
        Match(sequenceIndex, sequence.Length, (i, glyph) => CurrentFeature is not null && glyph.Features.Contains(CurrentFeature) && sequence[i] == glyph.Id);

    protected bool CoverageSequenceMatches(int sequenceIndex, Coverage[] sequence) =>
        Match(sequenceIndex, sequence.Length, (i, glyph) => sequence[i].IndexOf(glyph.Id) >= 0) is not null;

    protected bool ClassSequenceMatches(int sequenceIndex, ushort[] sequence, ClassDefinition classDef) =>
        Match(sequenceIndex, sequence.Length, (i, glyph) => sequence[i] == classDef.ClassOf(glyph.Id)) is not null;

    protected bool ApplyContext(object table)
    {
        switch (table)
        {
            case Context1 c1:
                {
                    var index = CoverageIndex(c1.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    foreach (var rule in c1.RuleSets[index])
                    {
                        if (SequenceMatches(1, rule.Input))
                        {
                            return ApplyLookupList(rule.LookupRecords);
                        }
                    }

                    return false;
                }

            case Context2 c2:
                {
                    if (CoverageIndex(c2.Coverage) == -1)
                    {
                        return false;
                    }

                    var index = c2.ClassDef.ClassOf(Iterator.Cur!.Id);
                    var set = index < c2.ClassSets.Length ? c2.ClassSets[index] : null;
                    if (set is null)
                    {
                        return false; // fontkit throws iterating a missing set; the fonts have none
                    }

                    foreach (var rule in set)
                    {
                        if (ClassSequenceMatches(1, rule.Input, c2.ClassDef))
                        {
                            return ApplyLookupList(rule.LookupRecords);
                        }
                    }

                    return false;
                }

            case Context3 c3:
                return CoverageSequenceMatches(0, c3.Coverages) && ApplyLookupList(c3.LookupRecords);
            default:
                return false;
        }
    }

    protected bool ApplyChainingContext(object table)
    {
        switch (table)
        {
            case Chain1 c1:
                {
                    var index = CoverageIndex(c1.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    foreach (var rule in c1.ChainRuleSets[index])
                    {
                        if (SequenceMatches(-rule.Backtrack.Length, rule.Backtrack) && SequenceMatches(1, rule.Input)
                            && SequenceMatches(1 + rule.Input.Length, rule.Lookahead))
                        {
                            return ApplyLookupList(rule.LookupRecords);
                        }
                    }

                    return false;
                }

            case Chain2 c2:
                {
                    if (CoverageIndex(c2.Coverage) == -1)
                    {
                        return false;
                    }

                    var index = c2.InputClassDef.ClassOf(Iterator.Cur!.Id);
                    var rules = index < c2.ChainClassSets.Length ? c2.ChainClassSets[index] : null;
                    if (rules is null)
                    {
                        return false;
                    }

                    foreach (var rule in rules)
                    {
                        if (ClassSequenceMatches(-rule.Backtrack.Length, rule.Backtrack, c2.BacktrackClassDef)
                            && ClassSequenceMatches(1, rule.Input, c2.InputClassDef)
                            && ClassSequenceMatches(1 + rule.Input.Length, rule.Lookahead, c2.LookaheadClassDef))
                        {
                            return ApplyLookupList(rule.LookupRecords);
                        }
                    }

                    return false;
                }

            case Chain3 c3:
                return CoverageSequenceMatches(-c3.BacktrackCoverage.Length, c3.BacktrackCoverage)
                    && CoverageSequenceMatches(0, c3.InputCoverage)
                    && CoverageSequenceMatches(c3.InputCoverage.Length, c3.LookaheadCoverage)
                    && ApplyLookupList(c3.LookupRecords);
            default:
                return false;
        }
    }

    private void ApplyLookups(IReadOnlyList<(string Feature, int Index, Lookup Lookup)> lookups, List<GlyphInfo> glyphs, List<GlyphPosition>? positions)
    {
        Glyphs = glyphs;
        Positions = positions;
        Iterator = new GlyphIterator(glyphs);
        foreach (var (feature, _, lookup) in lookups)
        {
            CurrentFeature = feature;
            Iterator.Reset(lookup.Flags);
            while (Iterator.Index < glyphs.Count)
            {
                if (!Iterator.Cur!.Features.Contains(feature))
                {
                    Iterator.Next();
                    continue;
                }

                foreach (var table in lookup.SubTables)
                {
                    if (ApplyLookup(lookup.LookupType, table))
                    {
                        break;
                    }
                }

                Iterator.Next();
            }
        }
    }

    private (string Tag, ScriptTable Script)? FindScript(IReadOnlyList<string>? scripts)
    {
        foreach (var s in scripts ?? [null!])
        {
            foreach (var entry in Table.Scripts)
            {
                if (entry.Tag == s)
                {
                    return entry;
                }
            }
        }

        return null;
    }
}

/// <summary>fontkit <c>GSUBProcessor</c>.</summary>
internal sealed class GsubProcessor(OpenTypeFont font, LayoutTable table) : OtProcessor(font, table)
{
    protected override bool ApplyLookup(int lookupType, object table)
    {
        switch (table)
        {
            case SingleSubst1 s1:
                {
                    if (CoverageIndex(s1.Coverage) == -1)
                    {
                        return false;
                    }

                    var glyph = Iterator.Cur!;
                    glyph.Id = (glyph.Id + s1.DeltaGlyphId) & 0xffff;
                    return true;
                }

            case SingleSubst2 s2:
                {
                    var index = CoverageIndex(s2.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    Iterator.Cur!.Id = s2.Substitutes[index];
                    return true;
                }

            case MultipleSubst multiple:
                {
                    var index = CoverageIndex(multiple.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    var sequence = multiple.Sequences[index];
                    var cur = Iterator.Cur!;
                    cur.Id = sequence[0];
                    cur.LigatureComponent = 0;
                    var replacement = sequence.Skip(1).Select((gid, i) => new GlyphInfo(Font, gid, null, cur.Features)
                    {
                        IsLigated = cur.IsLigated,
                        LigatureComponent = i + 1,
                    });
                    Glyphs.InsertRange(Iterator.Index + 1, replacement);
                    return true;
                }

            case AlternateSubst alternate:
                {
                    var index = CoverageIndex(alternate.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    Iterator.Cur!.Id = alternate.AlternateSets[index][0];
                    return true;
                }

            case LigatureSubst ligatures:
                return ApplyLigature(ligatures);
            case Extension extension:
                return ApplyLookup(extension.LookupType, extension.SubTable);
            default:
                return lookupType switch
                {
                    5 => ApplyContext(table),
                    6 => ApplyChainingContext(table),
                    _ => false,
                };
        }
    }

    private bool ApplyLigature(LigatureSubst table)
    {
        var index = CoverageIndex(table.Coverage);
        if (index == -1)
        {
            return false;
        }

        foreach (var ligature in table.LigatureSets[index])
        {
            var matched = SequenceMatchIndices(1, ligature.Components);
            if (matched is null)
            {
                continue;
            }

            var curGlyph = Iterator.Cur!;
            var characters = new List<int>(curGlyph.CodePoints);
            foreach (var i in matched)
            {
                characters.AddRange(Glyphs[i].CodePoints);
            }

            var ligatureGlyph = new GlyphInfo(Font, ligature.Glyph, [.. characters], curGlyph.Features) { IsLigated = true };
            var isMarkLigature = curGlyph.IsMark;
            for (var i = 0; i < matched.Count && isMarkLigature; i++)
            {
                isMarkLigature = Glyphs[matched[i]].IsMark;
            }

            ligatureGlyph.LigatureId = isMarkLigature ? null : NextLigatureId++;

            var lastLigId = curGlyph.LigatureId;
            var lastNumComps = curGlyph.CodePoints.Length;
            var curComps = lastNumComps;
            var idx = Iterator.Index + 1;
            foreach (var matchIndex in matched)
            {
                if (isMarkLigature)
                {
                    idx = matchIndex;
                }
                else
                {
                    while (idx < matchIndex)
                    {
                        var component = curComps - lastNumComps + Math.Min(Glyphs[idx].LigatureComponent is { } c and not 0 ? c : 1, lastNumComps);
                        Glyphs[idx].LigatureId = ligatureGlyph.LigatureId;
                        Glyphs[idx].LigatureComponent = component;
                        idx++;
                    }
                }

                lastLigId = Glyphs[idx].LigatureId;
                lastNumComps = Glyphs[idx].CodePoints.Length;
                curComps += lastNumComps;
                idx++;
            }

            if (lastLigId is not null and not 0 && !isMarkLigature)
            {
                for (var i = idx; i < Glyphs.Count; i++)
                {
                    if (Glyphs[i].LigatureId == lastLigId)
                    {
                        Glyphs[i].LigatureComponent = curComps - lastNumComps + Math.Min(Glyphs[i].LigatureComponent is { } c and not 0 ? c : 1, lastNumComps);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            for (var i = matched.Count - 1; i >= 0; i--)
            {
                Glyphs.RemoveAt(matched[i]);
            }

            Glyphs[Iterator.Index] = ligatureGlyph;
            return true;
        }

        return false;
    }
}

/// <summary>fontkit <c>GPOSProcessor</c> for the lookup types the fonts use (single and pair adjustment, contexts).</summary>
internal sealed class GposProcessor(OpenTypeFont font, LayoutTable table) : OtProcessor(font, table)
{
    protected override bool ApplyLookup(int lookupType, object table)
    {
        switch (table)
        {
            case SinglePos1 s1:
                if (CoverageIndex(s1.Coverage) == -1)
                {
                    return false;
                }

                ApplyPositionValue(0, s1.Value);
                return true;
            case SinglePos2 s2:
                {
                    var index = CoverageIndex(s2.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    ApplyPositionValue(0, s2.Values[index]);
                    return true;
                }

            case PairPos1 p1:
                {
                    var next = Iterator.Peek();
                    if (next is null)
                    {
                        return false;
                    }

                    var index = CoverageIndex(p1.Coverage);
                    if (index == -1)
                    {
                        return false;
                    }

                    foreach (var pair in p1.PairSets[index])
                    {
                        if (pair.SecondGlyph == next.Id)
                        {
                            ApplyPositionValue(0, pair.Value1);
                            ApplyPositionValue(1, pair.Value2);
                            return true;
                        }
                    }

                    return false;
                }

            case PairPos2 p2:
                {
                    var next = Iterator.Peek();
                    if (next is null || CoverageIndex(p2.Coverage) == -1)
                    {
                        return false;
                    }

                    var class1 = p2.ClassDef1.ClassOf(Iterator.Cur!.Id);
                    var class2 = p2.ClassDef2.ClassOf(next.Id);
                    var (value1, value2) = p2.ClassRecords[class1][class2];
                    ApplyPositionValue(0, value1);
                    ApplyPositionValue(1, value2);
                    return true;
                }

            case Extension extension:
                return ApplyLookup(extension.LookupType, extension.SubTable);
            default:
                return lookupType switch
                {
                    7 => ApplyContext(table),
                    8 => ApplyChainingContext(table),
                    _ => false, // cursive and mark attachment: not used by the fonts (see FontkitShaperTests)
                };
        }
    }

    private void ApplyPositionValue(int sequenceIndex, ValueRecord value)
    {
        var position = Positions![Iterator.PeekIndex(sequenceIndex)];
        if (value.XAdvance is { } xAdvance)
        {
            position.XAdvance += xAdvance;
        }

        if (value.YAdvance is { } yAdvance)
        {
            position.YAdvance += yAdvance;
        }

        if (value.XPlacement is { } xPlacement)
        {
            position.XOffset += xPlacement;
        }

        if (value.YPlacement is { } yPlacement)
        {
            position.YOffset += yPlacement;
        }
    }
}

/// <summary>Script detection and direction (fontkit <c>Script.forString</c>, <c>Script.direction</c>).</summary>
internal static class Scripts
{
    private static readonly HashSet<string> RightToLeft = new(StringComparer.Ordinal)
    {
        "arab", "hebr", "syrc", "thaa", "cprt", "khar", "phnx", "nko ", "lydi", "avst", "armi", "phli", "prti", "sarb", "orkh", "samr",
        "mand", "merc", "mero", "mani", "mend", "nbat", "narb", "palm", "phlp",
    };

    private static readonly IReadOnlyList<string> Unknown = ["Zzzz"];

    /// <summary>The first script of the string that is not Common/Inherited/Unknown; Unknown (<c>Zzzz</c>) if none; <c>null</c> (JavaScript <c>undefined</c>) at a script without a tag.</summary>
    public static IReadOnlyList<string>? ForString(string text)
    {
        var len = text.Length;
        var idx = 0;
        while (idx < len)
        {
            int code = text[idx++];
            if (code is >= 0xd800 and <= 0xdbff && idx < len)
            {
                int next = text[idx];
                if (next is >= 0xdc00 and <= 0xdfff)
                {
                    idx++;
                    code = ((code & 0x3ff) << 10) + (next & 0x3ff) + 0x10000;
                }
            }

            var script = UnicodeData.Script(code);
            if (ReferenceEquals(script, UnicodeData.UntaggedScript))
            {
                return null;
            }

            if (script is not null)
            {
                return script;
            }
        }

        return Unknown;
    }

    /// <summary><c>RTL[script]</c>: an array of tags (Indic scripts) is never right to left.</summary>
    public static string DirectionOf(IReadOnlyList<string>? script) => script is [var single] && RightToLeft.Contains(single) ? "rtl" : "ltr";
}

/// <summary>fontkit <c>ShapingPlan</c> + <c>DefaultShaper</c> (the only shaper the fonts' scripts select).</summary>
internal sealed class ShapingPlan
{
    private static readonly string[] LeftToRight = ["rvrn", "ltra", "ltrm"];
    private static readonly string[] RightToLeftFeatures = ["rvrn", "rtla", "rtlm"];
    private static readonly string[] Fractional = ["frac", "numr", "dnom"];
    private static readonly string[] CommonAndHorizontal = ["ccmp", "locl", "rlig", "mark", "mkmk", "calt", "clig", "liga", "rclt", "curs", "kern"];
    private static readonly string[] Numerator = ["numr", "frac"];
    private static readonly string[] Denominator = ["dnom", "frac"];
    private readonly List<List<string>> _stages = [];
    private readonly HashSet<string> _allFeatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalFeatures = new(StringComparer.Ordinal);

    public ShapingPlan(string direction, List<GlyphInfo> glyphs)
    {
        // DefaultShaper.plan: planPreprocessing, planFeatures (none), planPostprocessing (no user features).
        Add(direction == "rtl" ? RightToLeftFeatures : LeftToRight, global: true);
        Add(Fractional, global: false);
        Add(CommonAndHorizontal, global: true);
        foreach (var glyph in glyphs)
        {
            glyph.Features.UnionWith(_globalFeatures);
        }

        AssignFractions(glyphs);
    }

    public void Process(OtProcessor processor, List<GlyphInfo> glyphs, List<GlyphPosition>? positions)
    {
        foreach (var stage in _stages.Where(s => s.Count > 0))
        {
            processor.ApplyFeatures(stage, glyphs, positions);
        }
    }

    /// <summary><c>DefaultShaper.assignFeatures</c>: contextual fractions around U+2044.</summary>
    private static void AssignFractions(List<GlyphInfo> glyphs)
    {
        static bool IsDigit(GlyphInfo g) => g.CodePoints.Length > 0 && UnicodeData.IsDigit(g.CodePoints[0]);

        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            if (glyph.CodePoints.Length > 0 && glyph.CodePoints[0] == 0x2044)
            {
                var start = i;
                var end = i + 1;
                while (start > 0 && IsDigit(glyphs[start - 1]))
                {
                    glyphs[start - 1].Features.UnionWith(Numerator);
                    start--;
                }

                while (end < glyphs.Count && IsDigit(glyphs[end]))
                {
                    glyphs[end].Features.UnionWith(Denominator);
                    end++;
                }

                glyph.Features.Add("frac");
                i = end - 1;
            }
        }
    }

    private void Add(IEnumerable<string> features, bool global)
    {
        if (_stages.Count == 0)
        {
            _stages.Add([]);
        }

        foreach (var feature in features)
        {
            if (_allFeatures.Add(feature))
            {
                _stages[^1].Add(feature);
                if (global)
                {
                    _globalFeatures.Add(feature);
                }
            }
        }
    }
}

/// <summary>A shaped run: glyph ids with their positions (font units).</summary>
internal sealed record GlyphRun(IReadOnlyList<int> Glyphs, IReadOnlyList<GlyphPosition> Positions);

/// <summary>
/// One fontkit <c>TTFFont</c> object with its <c>LayoutEngine</c>: the state legacy pdfmake has per document and
/// font. fontkit caches glyph objects by id, and a glyph keeps the code points of its first lookup; that history
/// decides, e.g., whether a glyph shared by two code points is a mark or a default ignorable, so it is kept here.
/// </summary>
internal sealed class FontkitFont
{
    private readonly Dictionary<int, int[]> _glyphs = [];
    private GsubProcessor? _gsub;
    private GposProcessor? _gpos;
    private bool _engineCreated;

    public FontkitFont(OpenTypeFont font) => Font = font;

    public OpenTypeFont Font { get; }

    /// <summary><c>getGlyph(id, codePoints)</c>: the code points the glyph object was created with.</summary>
    public int[] GetGlyph(int id, int[]? codePoints = null)
    {
        if (!_glyphs.TryGetValue(id, out var known))
        {
            _glyphs[id] = known = codePoints ?? [];
        }

        return known;
    }

    /// <summary><c>font.layout(string)</c>: substitution, positioning, hidden default ignorables.</summary>
    public GlyphRun Layout(string text)
    {
        var script = Scripts.ForString(text);
        var glyphs = GlyphsForString(text);
        var direction = Scripts.DirectionOf(script);
        if (glyphs.Count == 0)
        {
            return new GlyphRun([], []);
        }

        if (!_engineCreated)
        {
            _engineCreated = true;
            _gsub = Font.Gsub is { } gsubTable ? new GsubProcessor(Font, gsubTable) : null;
            _gpos = Font.Gpos is { } gposTable ? new GposProcessor(Font, gposTable) : null;
        }

        // OTLayoutEngine.setup
        var infos = glyphs.Select(g => new GlyphInfo(Font, g, [.. GetGlyph(g)], null)).ToList();
        _gpos?.SelectScript(script, direction);
        _gsub?.SelectScript(script, direction);
        var plan = new ShapingPlan(direction, infos);

        // substitute
        var ids = new List<int>(glyphs);
        if (_gsub is not null)
        {
            plan.Process(_gsub, infos, null);
            ids = [.. infos.Select(info =>
            {
                GetGlyph(info.Id, info.CodePoints);
                return info.Id;
            })];
        }

        // position (DefaultShaper.zeroMarkWidths = AFTER_GPOS)
        var positions = ids.Select(id => new GlyphPosition(Font.AdvanceWidth(id))).ToList();
        if (_gpos is not null)
        {
            plan.Process(_gpos, infos, positions);
        }

        for (var i = 0; i < infos.Count; i++)
        {
            if (infos[i].IsMark)
            {
                positions[i].XAdvance = 0;
                positions[i].YAdvance = 0;
            }
        }

        if (direction == "rtl")
        {
            ids.Reverse();
            positions.Reverse();
        }

        // hideDefaultIgnorables, on the glyph objects' code points
        var space = Font.GlyphForCodePoint(0x20);
        GetGlyph(space, [0x20]);
        for (var i = 0; i < ids.Count; i++)
        {
            var codePoints = GetGlyph(ids[i]);
            if (codePoints.Length > 0 && IsDefaultIgnorable(codePoints[0]))
            {
                ids[i] = space;
                positions[i].XAdvance = 0;
                positions[i].YAdvance = 0;
            }
        }

        return new GlyphRun(ids, positions);
    }

    /// <summary><c>glyphsForString</c>: one glyph per code point; a variation selector joins the code point before it.</summary>
    private List<int> GlyphsForString(string text)
    {
        var glyphs = new List<int>();
        var len = text.Length;
        var idx = 0;
        var last = -1;
        var state = -1;
        while (idx <= len)
        {
            var code = 0;
            var nextState = 0;
            if (idx < len)
            {
                code = text[idx++];
                if (code is >= 0xd800 and <= 0xdbff && idx < len)
                {
                    int next = text[idx];
                    if (next is >= 0xdc00 and <= 0xdfff)
                    {
                        idx++;
                        code = ((code & 0x3ff) << 10) + (next & 0x3ff) + 0x10000;
                    }
                }

                nextState = code is >= 0xfe00 and <= 0xfe0f or >= 0xe0100 and <= 0xe01ef ? 1 : 0;
            }
            else
            {
                idx++;
            }

            if (state == 0 && nextState == 1)
            {
                var id = Font.GlyphForCodePoint(last); // no format 14 cmap: the variation selector is not used
                GetGlyph(id, [last, code]);
                glyphs.Add(id);
            }
            else if (state == 0 && nextState == 0)
            {
                var id = Font.GlyphForCodePoint(last);
                GetGlyph(id, [last]);
                glyphs.Add(id);
            }

            last = code;
            state = nextState;
        }

        return glyphs;
    }

    /// <summary><c>isDefaultIgnorable</c> (DerivedCoreProperties minus U+115F, U+1160, U+3164, U+FFA0, as HarfBuzz).</summary>
    private static bool IsDefaultIgnorable(int ch)
    {
        var plane = ch >> 16;
        if (plane == 0)
        {
            return (ch >> 8) switch
            {
                0x00 => ch == 0x00AD,
                0x03 => ch == 0x034F,
                0x06 => ch == 0x061C,
                0x17 => ch is >= 0x17B4 and <= 0x17B5,
                0x18 => ch is >= 0x180B and <= 0x180E,
                0x20 => ch is >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E or >= 0x2060 and <= 0x206F,
                0xFE => ch is >= 0xFE00 and <= 0xFE0F or 0xFEFF,
                0xFF => ch is >= 0xFFF0 and <= 0xFFF8,
                _ => false,
            };
        }

        return plane switch
        {
            0x01 => ch is >= 0x1BCA0 and <= 0x1BCA3 or >= 0x1D173 and <= 0x1D17A,
            0x0E => ch is >= 0xE0000 and <= 0xE0FFF,
            _ => false,
        };
    }
}
