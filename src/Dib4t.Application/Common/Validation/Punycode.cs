using System.Text;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// Port of Node's built-in <c>punycode</c> 2.1.0 <c>toASCII</c> (RFC 3492), which the legacy e-mail check uses for
/// domain and label lengths (<see cref="LegacyEmail"/>). Not .NET's <c>IdnMapping</c>: that one validates and maps
/// (IDNA 2008) where punycode.js only encodes, so lengths could differ.
/// </summary>
internal static class Punycode
{
    private const int MaxInt = int.MaxValue;
    private const int Base = 36;
    private const int TMin = 1;
    private const int TMax = 26;
    private const int Skew = 38;
    private const int Damp = 700;
    private const int InitialBias = 72;
    private const int InitialN = 128;

    /// <summary>
    /// punycode.js <c>mapDomain</c>: keeps what is before the first <c>@</c> and drops everything after a second one
    /// (<c>split('@')</c>), maps the label separators <c>. 。 ． ｡</c> to <c>.</c>, and encodes each label that has a
    /// character above U+007E as <c>xn--…</c>.
    /// </summary>
    public static string ToAscii(string input)
    {
        var parts = input.Split('@');
        var result = string.Empty;
        var domain = input;
        if (parts.Length > 1)
        {
            result = parts[0] + "@";
            domain = parts[1];
        }

        domain = domain.Replace('。', '.').Replace('．', '.').Replace('｡', '.');
        var labels = domain.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i].Any(c => c > '~'))
            {
                labels[i] = "xn--" + Encode(labels[i]);
            }
        }

        return result + string.Join('.', labels);
    }

    /// <summary>punycode.js <c>encode</c>: code points as <c>ucs2decode</c> reads them (a lone surrogate counts as one).</summary>
    public static string Encode(string input)
    {
        var codePoints = Ucs2Decode(input);
        var output = new StringBuilder();
        foreach (var value in codePoints)
        {
            if (value < 0x80)
            {
                output.Append((char)value);
            }
        }

        var basicLength = output.Length;
        var handledCount = basicLength;
        if (basicLength > 0)
        {
            output.Append('-');
        }

        var n = InitialN;
        long delta = 0;
        var bias = InitialBias;
        while (handledCount < codePoints.Count)
        {
            var m = MaxInt;
            foreach (var value in codePoints)
            {
                if (value >= n && value < m)
                {
                    m = value;
                }
            }

            var handledPlusOne = handledCount + 1;
            if (m - n > (MaxInt - delta) / handledPlusOne)
            {
                throw new OverflowException("punycode overflow");
            }

            delta += (long)(m - n) * handledPlusOne;
            n = m;
            foreach (var value in codePoints)
            {
                if (value < n && ++delta > MaxInt)
                {
                    throw new OverflowException("punycode overflow");
                }

                if (value != n)
                {
                    continue;
                }

                var q = delta;
                for (var k = Base; ; k += Base)
                {
                    long t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                    if (q < t)
                    {
                        break;
                    }

                    output.Append(DigitToBasic((int)(t + ((q - t) % (Base - t)))));
                    q = (q - t) / (Base - t);
                }

                output.Append(DigitToBasic((int)q));
                bias = Adapt(delta, handledPlusOne, handledCount == basicLength);
                delta = 0;
                handledCount++;
            }

            delta++;
            n++;
        }

        return output.ToString();
    }

    private static List<int> Ucs2Decode(string input)
    {
        var output = new List<int>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                output.Add(char.ConvertToUtf32(c, input[++i]));
            }
            else
            {
                output.Add(c);
            }
        }

        return output;
    }

    private static char DigitToBasic(int digit) => (char)(digit + 22 + (digit < 26 ? 75 : 0));

    private static int Adapt(long delta, int numPoints, bool firstTime)
    {
        var k = 0;
        delta = firstTime ? delta / Damp : delta >> 1;
        delta += delta / numPoints;
        for (; delta > ((Base - TMin) * TMax) >> 1; k += Base)
        {
            delta /= Base - TMin;
        }

        return (int)(k + ((Base - TMin + 1) * delta / (delta + Skew)));
    }
}
