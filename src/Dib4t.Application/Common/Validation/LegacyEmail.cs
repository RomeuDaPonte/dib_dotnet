using System.Text;
using System.Text.RegularExpressions;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// The legacy e-mail rule: Joi 14 <c>string().email()</c> = <b>isemail 3.2.0</b> <c>validate(value)</c> with default
/// options, ported statement by statement (<c>back-end/node_modules/isemail/lib/index.js</c>). Valid means the
/// worst diagnosis is below 16: plain RFC 5321 mailboxes, quoted local parts and address literals pass; comments,
/// folding white space, obsolete forms and anything invalid fail. Checked against the legacy code's own answers in
/// <c>tests/fixtures/legacy/auth/auth-golden.json</c>.
/// </summary>
/// <remarks>
/// Only what changes the yes/no answer is ported: the TLD-numeric hint (diagnosis 10) and the threshold/diagnose
/// options cannot make an address invalid with Joi's defaults. JavaScript semantics kept: the input is NFC-normalized
/// first, characters are read by code point, lengths are UTF-8 bytes (<c>Buffer.byteLength</c>) or punycode lengths,
/// and a CR that is not followed by LF also skips the next character (the legacy <c>++i</c>).
/// </remarks>
internal static partial class LegacyEmail
{
    private const int DefaultThreshold = 16;

    // Categories.
    private const int DeprecatedCategory = 63;
    private const int Rfc5322Category = 127;

    // Diagnoses.
    private const int Rfc5321QuotedString = 11;
    private const int Rfc5321AddressLiteral = 12;
    private const int CfwsComment = 17;
    private const int CfwsFws = 18;
    private const int DeprecatedLocalPart = 33;
    private const int DeprecatedFws = 34;
    private const int DeprecatedQText = 35;
    private const int DeprecatedQp = 36;
    private const int DeprecatedComment = 37;
    private const int DeprecatedCText = 38;
    private const int DeprecatedIpv6 = 39;
    private const int DeprecatedCfwsNearAt = 49;
    private const int Rfc5322Domain = 65;
    private const int Rfc5322TooLong = 66;
    private const int Rfc5322LocalTooLong = 67;
    private const int Rfc5322DomainTooLong = 68;
    private const int Rfc5322LabelTooLong = 69;
    private const int Rfc5322DomainLiteral = 70;
    private const int Rfc5322DomainLiteralObsDText = 71;
    private const int Rfc5322Ipv6GroupCount = 72;
    private const int Rfc5322Ipv62X2XColon = 73;
    private const int Rfc5322Ipv6BadCharacter = 74;
    private const int Rfc5322Ipv6MaxGroups = 75;
    private const int Rfc5322Ipv6ColonStart = 76;
    private const int Rfc5322Ipv6ColonEnd = 77;
    private const int ErrExpectingDText = 129;
    private const int ErrNoLocalPart = 130;
    private const int ErrNoDomain = 131;
    private const int ErrConsecutiveDots = 132;
    private const int ErrAtextAfterCfws = 133;
    private const int ErrAtextAfterQs = 134;
    private const int ErrAtextAfterDomainLiteral = 135;
    private const int ErrExpectingQPair = 136;
    private const int ErrExpectingAtext = 137;
    private const int ErrExpectingQText = 138;
    private const int ErrExpectingCText = 139;
    private const int ErrBackslashEnd = 140;
    private const int ErrDotStart = 141;
    private const int ErrDotEnd = 142;
    private const int ErrDomainHyphenStart = 143;
    private const int ErrDomainHyphenEnd = 144;
    private const int ErrUnclosedQuotedString = 145;
    private const int ErrUnclosedComment = 146;
    private const int ErrUnclosedDomainLiteral = 147;
    private const int ErrFwsCrlfX2 = 148;
    private const int ErrFwsCrlfEnd = 149;
    private const int ErrCrNoLf = 150;
    private const int ErrDotAfterDomainLiteral = 162;

    private const string Specials = "()<>[]:;@\\,.\"";

    private enum Component
    {
        LocalPart,
        Domain,
        Literal,
        ContextComment,
        ContextFws,
        ContextQuotedString,
        ContextQuotedPair,
    }

    public static bool IsValid(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        try
        {
            return Diagnose(Normalize(email)) < DefaultThreshold;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        {
            // Joi wraps isemail in try/catch: an internal error (unexpected context, punycode overflow) is "invalid".
            return false;
        }
    }

    /// <summary>isemail's worst diagnosis for <paramref name="email"/> (already NFC).</summary>
    private static int Diagnose(string email)
    {

        var maxResult = 0;
        void Update(int value)
        {
            if (value > maxResult)
            {
                maxResult = value;
            }
        }

        var now = Component.LocalPart;
        var prev = Component.LocalPart;
        var stack = new Stack<Component>();
        stack.Push(Component.LocalPart);

        var prevToken = string.Empty;
        var local = new StringBuilder();
        var domain = new StringBuilder();
        var literal = new StringBuilder();
        var locals = new List<StringBuilder> { new() };
        var domains = new List<StringBuilder> { new() };

        var elementCount = 0;
        var elementLength = 0;
        var crlfCount = 0;
        var hyphenFlag = false;
        var assertEnd = false;
        var length = email.Length;
        var token = string.Empty;

        void SetAtom(List<StringBuilder> atoms, int index)
        {
            while (atoms.Count <= index)
            {
                atoms.Add(new StringBuilder());
            }

            atoms[index].Clear();
        }

        // JavaScript: `if (emailLength === ++i || email[i] !== '\n')` → true means "CR without LF" (and i moved on).
        bool CrWithoutLf(ref int index) => ++index == length || email[index] != '\n';

        for (var i = 0; i < length; i += token.Length)
        {
            token = TokenAt(email, i);
            var code = CodePoint(token);

            switch (now)
            {
                case Component.LocalPart:
                    switch (token)
                    {
                        case "(":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? CfwsComment : DeprecatedComment);
                            }
                            else
                            {
                                Update(CfwsComment);
                                assertEnd = true;
                            }

                            stack.Push(now);
                            now = Component.ContextComment;
                            break;

                        case ".":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? ErrDotStart : ErrConsecutiveDots);
                            }
                            else
                            {
                                if (assertEnd)
                                {
                                    Update(DeprecatedLocalPart);
                                }

                                assertEnd = false;
                                elementLength = 0;
                                elementCount++;
                                local.Append(token);
                                SetAtom(locals, elementCount);
                            }

                            break;

                        case "\"":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? Rfc5321QuotedString : DeprecatedLocalPart);
                                local.Append(token);
                                locals[elementCount].Append(token);
                                elementLength += ByteLength(token);
                                assertEnd = true;
                                stack.Push(now);
                                now = Component.ContextQuotedString;
                            }
                            else
                            {
                                Update(ErrExpectingAtext);
                            }

                            break;

                        case "\r" when CrWithoutLf(ref i):
                            Update(ErrCrNoLf);
                            break;

                        case "\r" or " " or "\t":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? CfwsFws : DeprecatedFws);
                            }
                            else
                            {
                                assertEnd = true;
                            }

                            stack.Push(now);
                            now = Component.ContextFws;
                            prevToken = token;
                            break;

                        case "@":
                            if (local.Length == 0)
                            {
                                Update(ErrNoLocalPart);
                            }
                            else if (elementLength == 0)
                            {
                                Update(ErrDotEnd);
                            }
                            else if (ByteLength(local.ToString()) > 64)
                            {
                                Update(Rfc5322LocalTooLong);
                            }
                            else if (prev is Component.ContextComment or Component.ContextFws)
                            {
                                Update(DeprecatedCfwsNearAt);
                            }

                            now = Component.Domain;
                            stack.Clear();
                            stack.Push(Component.Domain);
                            elementCount = 0;
                            elementLength = 0;
                            assertEnd = false;
                            break;

                        default:
                            if (assertEnd)
                            {
                                Update(prev switch
                                {
                                    Component.ContextComment or Component.ContextFws => ErrAtextAfterCfws,
                                    Component.ContextQuotedString => ErrAtextAfterQs,
                                    _ => throw new InvalidOperationException("more atext found where none is allowed, but unrecognized prev context: " + prev),
                                });
                            }
                            else
                            {
                                prev = now;
                                if (IsSpecial(code) || IsC0Control(code) || IsC1Control(code))
                                {
                                    Update(ErrExpectingAtext);
                                }

                                local.Append(token);
                                locals[elementCount].Append(token);
                                elementLength += ByteLength(token);
                            }

                            break;
                    }

                    break;

                case Component.Domain:
                    switch (token)
                    {
                        case "(":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? DeprecatedCfwsNearAt : DeprecatedComment);
                            }
                            else
                            {
                                assertEnd = true;
                                Update(CfwsComment);
                            }

                            stack.Push(now);
                            now = Component.ContextComment;
                            break;

                        case ".":
                            var labelLength = Punycode.ToAscii(domains[elementCount].ToString()).Length;
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? ErrDotStart : ErrConsecutiveDots);
                            }
                            else if (hyphenFlag)
                            {
                                Update(ErrDomainHyphenEnd);
                            }
                            else if (labelLength > 63)
                            {
                                Update(Rfc5322LabelTooLong);
                            }

                            assertEnd = false;
                            elementLength = 0;
                            elementCount++;
                            SetAtom(domains, elementCount);
                            domain.Append(token);
                            break;

                        case "[":
                            if (domains[elementCount].Length == 0)
                            {
                                if (domain.Length > 0)
                                {
                                    Update(ErrDotAfterDomainLiteral);
                                }

                                assertEnd = true;
                                elementLength += ByteLength(token);
                                stack.Push(now);
                                now = Component.Literal;
                                domain.Append(token);
                                domains[elementCount].Append(token);
                                literal.Clear();
                            }
                            else
                            {
                                Update(ErrExpectingAtext);
                            }

                            break;

                        case "\r" when CrWithoutLf(ref i):
                            Update(ErrCrNoLf);
                            break;

                        case "\r" or " " or "\t":
                            if (elementLength == 0)
                            {
                                Update(elementCount == 0 ? DeprecatedCfwsNearAt : DeprecatedFws);
                            }
                            else
                            {
                                Update(CfwsFws);
                                assertEnd = true;
                            }

                            stack.Push(now);
                            now = Component.ContextFws;
                            prevToken = token;
                            break;

                        default:
                            if (assertEnd)
                            {
                                Update(prev switch
                                {
                                    Component.ContextComment or Component.ContextFws => ErrAtextAfterCfws,
                                    Component.Literal => ErrAtextAfterDomainLiteral,
                                    _ => throw new InvalidOperationException("more atext found where none is allowed, but unrecognized prev context: " + prev),
                                });
                            }

                            hyphenFlag = false;
                            if (IsSpecial(code) || IsC0Control(code) || IsC1Control(code))
                            {
                                Update(ErrExpectingAtext);
                            }
                            else if (token == "-")
                            {
                                if (elementLength == 0)
                                {
                                    Update(ErrDomainHyphenStart);
                                }

                                hyphenFlag = true;
                            }
                            else if (code < 48 || (code > 122 && code < 192) || (code > 57 && code < 65) || (code > 90 && code < 97))
                            {
                                Update(Rfc5322Domain);
                            }

                            domain.Append(token);
                            domains[elementCount].Append(token);
                            elementLength += ByteLength(token);
                            break;
                    }

                    break;

                case Component.Literal:
                    switch (token)
                    {
                        case "]":
                            if (maxResult < DeprecatedCategory)
                            {
                                CheckAddressLiteral(literal.ToString(), Update);
                            }
                            else
                            {
                                Update(Rfc5322DomainLiteral);
                            }

                            domain.Append(token);
                            domains[elementCount].Append(token);
                            elementLength += ByteLength(token);
                            prev = now;
                            now = stack.Pop();
                            break;

                        case "\\":
                            Update(Rfc5322DomainLiteralObsDText);
                            stack.Push(now);
                            now = Component.ContextQuotedPair;
                            break;

                        case "\r" when CrWithoutLf(ref i):
                            Update(ErrCrNoLf);
                            break;

                        case "\r" or " " or "\t":
                            Update(CfwsFws);
                            stack.Push(now);
                            now = Component.ContextFws;
                            prevToken = token;
                            break;

                        default:
                            if ((code != 127 && IsC1Control(code)) || code == 0 || token == "[")
                            {
                                Update(ErrExpectingDText);
                                break;
                            }

                            if (IsC0Control(code) || code == 127)
                            {
                                Update(Rfc5322DomainLiteralObsDText);
                            }

                            literal.Append(token);
                            domain.Append(token);
                            domains[elementCount].Append(token);
                            elementLength += ByteLength(token);
                            break;
                    }

                    break;

                case Component.ContextQuotedString:
                    switch (token)
                    {
                        case "\\":
                            stack.Push(now);
                            now = Component.ContextQuotedPair;
                            break;

                        case "\r" when CrWithoutLf(ref i):
                            Update(ErrCrNoLf);
                            break;

                        case "\r" or "\t":
                            local.Append(' ');
                            locals[elementCount].Append(' ');
                            elementLength += ByteLength(token);
                            Update(CfwsFws);
                            stack.Push(now);
                            now = Component.ContextFws;
                            prevToken = token;
                            break;

                        case "\"":
                            local.Append(token);
                            locals[elementCount].Append(token);
                            elementLength += ByteLength(token);
                            prev = now;
                            now = stack.Pop();
                            break;

                        default:
                            if ((code != 127 && IsC1Control(code)) || code == 0 || code == 10)
                            {
                                Update(ErrExpectingQText);
                            }
                            else if (IsC0Control(code) || code == 127)
                            {
                                Update(DeprecatedQText);
                            }

                            local.Append(token);
                            locals[elementCount].Append(token);
                            elementLength += ByteLength(token);
                            break;
                    }

                    break;

                case Component.ContextQuotedPair:
                    if (code != 127 && IsC1Control(code))
                    {
                        Update(ErrExpectingQPair);
                    }
                    else if ((code < 31 && code != 9) || code == 127)
                    {
                        Update(DeprecatedQp);
                    }

                    prev = now;
                    now = stack.Pop();
                    var escapeToken = "\\" + token;
                    switch (now)
                    {
                        case Component.ContextComment:
                            break;
                        case Component.ContextQuotedString:
                            local.Append(escapeToken);
                            locals[elementCount].Append(escapeToken);
                            elementLength += 2;
                            break;
                        case Component.Literal:
                            domain.Append(escapeToken);
                            domains[elementCount].Append(escapeToken);
                            elementLength += 2;
                            break;
                        default:
                            throw new InvalidOperationException("quoted pair logic invoked in an invalid context: " + now);
                    }

                    break;

                case Component.ContextComment:
                    switch (token)
                    {
                        case "(":
                            stack.Push(now);
                            now = Component.ContextComment;
                            break;

                        case ")":
                            prev = now;
                            now = stack.Pop();
                            break;

                        case "\\":
                            stack.Push(now);
                            now = Component.ContextQuotedPair;
                            break;

                        case "\r" when CrWithoutLf(ref i):
                            Update(ErrCrNoLf);
                            break;

                        case "\r" or " " or "\t":
                            Update(CfwsFws);
                            stack.Push(now);
                            now = Component.ContextFws;
                            prevToken = token;
                            break;

                        default:
                            if (code == 0 || code == 10 || (code != 127 && IsC1Control(code)))
                            {
                                Update(ErrExpectingCText);
                                break;
                            }

                            if (IsC0Control(code) || code == 127)
                            {
                                Update(DeprecatedCText);
                            }

                            break;
                    }

                    break;

                case Component.ContextFws:
                    if (prevToken == "\r")
                    {
                        if (token == "\r")
                        {
                            Update(ErrFwsCrlfX2);
                            break;
                        }

                        if (++crlfCount > 1)
                        {
                            Update(DeprecatedFws);
                        }
                        else
                        {
                            crlfCount = 1;
                        }
                    }

                    switch (token)
                    {
                        case "\r":
                            if (CrWithoutLf(ref i))
                            {
                                Update(ErrCrNoLf);
                            }

                            break;

                        case " " or "\t":
                            break;

                        default:
                            if (prevToken == "\r")
                            {
                                Update(ErrFwsCrlfEnd);
                            }

                            crlfCount = 0;
                            prev = now;
                            now = stack.Pop();
                            i--; // read this token again in the context below
                            break;
                    }

                    prevToken = token;
                    break;
            }

            if (maxResult > Rfc5322Category)
            {
                break; // fatal error, no point continuing
            }
        }

        if (maxResult < Rfc5322Category)
        {
            var domainText = domain.ToString();
            var punycodeLength = Punycode.ToAscii(domainText).Length;
            if (now == Component.ContextQuotedString)
            {
                Update(ErrUnclosedQuotedString);
            }
            else if (now == Component.ContextQuotedPair)
            {
                Update(ErrBackslashEnd);
            }
            else if (now == Component.ContextComment)
            {
                Update(ErrUnclosedComment);
            }
            else if (now == Component.Literal)
            {
                Update(ErrUnclosedDomainLiteral);
            }
            else if (token == "\r")
            {
                Update(ErrFwsCrlfEnd);
            }
            else if (domainText.Length == 0)
            {
                Update(ErrNoDomain);
            }
            else if (elementLength == 0)
            {
                Update(ErrDotEnd);
            }
            else if (hyphenFlag)
            {
                Update(ErrDomainHyphenEnd);
            }
            else if (punycodeLength > 255)
            {
                Update(Rfc5322DomainTooLong);
            }
            else if (ByteLength(local.ToString()) + punycodeLength + 1 > 254)
            {
                Update(Rfc5322TooLong);
            }
            else if (elementLength > 63)
            {
                Update(Rfc5322LabelTooLong);
            }
            else if (HasDomainLiteralThenAtom(domains))
            {
                Update(ErrDotAfterDomainLiteral);
            }
        }

        // isemail then adds the numeric-TLD hint (10) when maxResult < 7: below the threshold, so never decisive.
        return maxResult;
    }

    private static void CheckAddressLiteral(string addressLiteral, Action<int> update)
    {
        var index = -1;
        var ipv4 = Ipv4Regex().Match(addressLiteral);
        if (ipv4.Success)
        {
            index = ipv4.Index;
            if (index != 0)
            {
                addressLiteral = addressLiteral[..index] + "0:0";
            }
        }

        if (index == 0)
        {
            update(Rfc5321AddressLiteral);
            return;
        }

        if (!addressLiteral[..Math.Min(5, addressLiteral.Length)].Equals("ipv6:", StringComparison.OrdinalIgnoreCase))
        {
            update(Rfc5322DomainLiteral);
            return;
        }

        var match = addressLiteral[5..];
        var maxGroups = 8;
        var groups = match.Split(':');
        index = match.IndexOf("::", StringComparison.Ordinal);
        if (index < 0)
        {
            if (groups.Length != maxGroups)
            {
                update(Rfc5322Ipv6GroupCount);
            }
        }
        else if (index != match.LastIndexOf("::", StringComparison.Ordinal))
        {
            update(Rfc5322Ipv62X2XColon);
        }
        else
        {
            if (index == 0 || index == match.Length - 2)
            {
                maxGroups++;
            }

            if (groups.Length > maxGroups)
            {
                update(Rfc5322Ipv6MaxGroups);
            }
            else if (groups.Length == maxGroups)
            {
                update(DeprecatedIpv6);
            }
        }

        // JavaScript: match[0], match[1] are undefined past the end; `undefined !== ':'` is true.
        static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';
        if (At(match, 0) == ':' && At(match, 1) != ':')
        {
            update(Rfc5322Ipv6ColonStart);
        }
        else if (At(match, match.Length - 1) == ':' && At(match, match.Length - 2) != ':')
        {
            update(Rfc5322Ipv6ColonEnd);
        }
        else if (groups.All(group => group.Length <= 4 && group.All(char.IsAsciiHexDigit)))
        {
            update(Rfc5321AddressLiteral);
        }
        else
        {
            update(Rfc5322Ipv6BadCharacter);
        }
    }

    private static bool HasDomainLiteralThenAtom(List<StringBuilder> domains)
    {
        var hasDomainLiteral = false;
        foreach (var atom in domains)
        {
            if (atom.Length > 0 && atom[0] == '[')
            {
                hasDomainLiteral = true;
            }
            else if (hasDomainLiteral)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>JavaScript <c>String.prototype.normalize('NFC')</c>, which leaves lone surrogates alone (.NET throws).</summary>
    private static string Normalize(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary><c>String.fromCodePoint(email.codePointAt(i))</c>: a surrogate pair, or one UTF-16 unit.</summary>
    private static string TokenAt(string value, int index) =>
        char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
            ? value.Substring(index, 2)
            : value.Substring(index, 1);

    /// <summary><c>Buffer.byteLength(s, 'utf8')</c>: a lone surrogate is written as U+FFFD (3 bytes), as .NET does.</summary>
    private static int ByteLength(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>The code point of a one- or two-unit token (a lone surrogate is its own value).</summary>
    private static int CodePoint(string token) => token.Length == 2 ? char.ConvertToUtf32(token[0], token[1]) : token[0];

    private static bool IsSpecial(int code) => code < 0x80 && Specials.Contains((char)code, StringComparison.Ordinal);

    /// <summary>isemail's lookup tables cover 0–255 only: U+0000–U+0020.</summary>
    private static bool IsC0Control(int code) => code is >= 0 and < 33;

    /// <summary>U+007F–U+009F.</summary>
    private static bool IsC1Control(int code) => code is >= 127 and < 160;

    /// <summary>
    /// <c>/\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)$/</c> with JavaScript's ASCII
    /// <c>\b</c> and <c>\d</c>, and <c>$</c> = end of input.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();
}
