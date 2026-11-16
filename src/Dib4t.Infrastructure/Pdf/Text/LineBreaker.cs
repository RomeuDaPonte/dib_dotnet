namespace Dib4t.Infrastructure.Pdf.Text;

/// <summary>A break opportunity: the text before <see cref="Position"/> (UTF-16 index) can end a line.</summary>
internal readonly record struct LineBreak(int Position, bool Required);

/// <summary>
/// Port of the <c>linebreak</c> package bundled in the legacy pdfmake 0.1.37 (a UAX #14 pair-table line breaker),
/// statement for statement, including its quirks (checked against the bundle in <c>line-breaks.json</c>).
/// </summary>
internal sealed class LineBreaker(string text)
{
    // Class constants (classes.js) used by the algorithm.
    private const int Al = 12;
    private const int Ns = 5;
    private const int Ba = 17;
    private const int Wj = 22;
    private const int Ai = 29;
    private const int Bk = 30;
    private const int Cb = 31;
    private const int Cj = 32;
    private const int Cr = 33;
    private const int Lf = 34;
    private const int Nl = 35;
    private const int Sa = 36;
    private const int Sg = 37;
    private const int Sp = 38;
    private const int Xx = 39;

    // pairs.js: DI_BRK 0, IN_BRK 1, CI_BRK 2, CP_BRK 3, PR_BRK 4; rows = current class, columns = next class.
    private static readonly string[] PairTable =
    [
        "44444444444444444444434444444",
        "04411444411000001100424000000",
        "04411444411111001100424000000",
        "44411144411111111111424111111",
        "14411144411111111111424111111",
        "04411144400000001100424000000",
        "04411144400000001100424000000",
        "04411144400100001100424000000",
        "04411144400111001100424000000",
        "14411144400111101100424111110",
        "14411144400111001100424000000",
        "14411144411111011100424000000",
        "14411144400111011100424000000",
        "14411144400111011100424000000",
        "04411144401000011100424000000",
        "04411144400000011100424000000",
        "04410144400100001100424000000",
        "04410144400000001100424000000",
        "14411144411111111111424111111",
        "04411144400000001104424000000",
        "00000000000000000000400000000",
        "14411144400111011100424000000",
        "14411144411111111111424111111",
        "04411144401000011100424000110",
        "04411144401000011100424000010",
        "04411144401000011100424111100",
        "04411144401000011100424000110",
        "04411144401000011100424000010",
        "04411144400000001100424000001",
    ];

    private readonly string _text = text;
    private int _pos;
    private int _lastPos;
    private int? _curClass;
    private int? _nextClass;

    /// <summary>All the breaks of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<LineBreak> All(string text)
    {
        var breaker = new LineBreaker(text);
        var result = new List<LineBreak>();
        while (breaker.NextBreak() is { } next)
        {
            result.Add(next);
        }

        return result;
    }

    /// <summary><c>nextBreak()</c>: the next break, or <c>null</c> at the end.</summary>
    public LineBreak? NextBreak()
    {
        _curClass ??= MapFirst(NextCharClass());

        while (_pos < _text.Length)
        {
            _lastPos = _pos;
            var lastClass = _nextClass;
            _nextClass = NextCharClass();
            var next = _nextClass.Value;

            if (_curClass == Bk || (_curClass == Cr && next != Lf))
            {
                _curClass = MapFirst(MapClass(next));
                return new LineBreak(_lastPos, true);
            }

            int? cur = next switch
            {
                Sp => _curClass,
                Bk or Lf or Nl => Bk,
                Cr => Cr,
                Cb => Ba,
                _ => null,
            };
            if (cur is not null)
            {
                _curClass = cur;
                if (next == Cb)
                {
                    return new LineBreak(_lastPos, false);
                }

                continue;
            }

            var shouldBreak = false;
            switch (Pair(_curClass!.Value, next))
            {
                case 0: // DI_BRK
                    shouldBreak = true;
                    break;
                case 1: // IN_BRK
                    shouldBreak = lastClass == Sp;
                    break;
                case 2: // CI_BRK
                    shouldBreak = lastClass == Sp;
                    if (!shouldBreak)
                    {
                        continue;
                    }

                    break;
                case 3: // CP_BRK
                    if (lastClass != Sp)
                    {
                        continue;
                    }

                    break;
                default:
                    break;
            }

            _curClass = next;
            if (shouldBreak)
            {
                return new LineBreak(_lastPos, false);
            }
        }

        if (_lastPos < _text.Length)
        {
            _lastPos = _text.Length;
            return new LineBreak(_text.Length, false);
        }

        return null;
    }

    /// <summary><c>pairTable[cur][next]</c>; <c>-1</c> where JavaScript reads <c>undefined</c> (no case matches).</summary>
    private static int Pair(int current, int next) =>
        current >= 0 && current < PairTable.Length && next >= 0 && next < PairTable[current].Length ? PairTable[current][next] - '0' : -1;

    private int NextCharClass() => MapClass(UnicodeData.LineBreakClass(NextCodePoint()));

    private int NextCodePoint()
    {
        int code = _pos < _text.Length ? _text[_pos] : -1; // charCodeAt past the end: NaN
        _pos++;
        int next = _pos < _text.Length ? _text[_pos] : -1;
        if (code is >= 0xd800 and <= 0xdbff && next is >= 0xdc00 and <= 0xdfff)
        {
            _pos++;
            return ((code - 0xd800) * 0x400) + (next - 0xdc00) + 0x10000;
        }

        return code;
    }

    private static int MapClass(int c) => c switch
    {
        Ai => Al,
        Sa or Sg or Xx => Al,
        Cj => Ns,
        _ => c,
    };

    private static int MapFirst(int c) => c switch
    {
        Lf or Nl => Bk,
        Cb => Ba,
        Sp => Wj,
        _ => c,
    };
}
