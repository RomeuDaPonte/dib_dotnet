using Dib4t.Application.Common.Validation;

namespace Dib4t.Application.Tests.Common;

/// <summary>The isemail 3.2.0 port gives the legacy answer for every e-mail of the golden file.</summary>
public sealed class LegacyEmailTests
{
    public static TheoryData<string, bool> GoldenEmails
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var (email, valid) in AuthGolden.Emails)
            {
                data.Add(email, valid);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenEmails))]
    public void Gives_the_legacy_answer(string email, bool valid) =>
        LegacyEmail.IsValid(email).Should().Be(valid, "isemail says {0} for {1}", valid, Escape(email));

    /// <summary>Not in the golden file (its JSON reader refuses lone surrogates); answers taken from Node 16 + Joi 14.</summary>
    [Theory]
    [InlineData("\ud800@x.pt")]
    [InlineData("a@x\ud800.pt")]
    public void Lone_surrogates_are_read_like_JavaScript(string email) =>
        LegacyEmail.IsValid(email).Should().BeTrue();

    [Fact]
    public void Punycode_encodes_like_punycode_js()
    {
        // Expected values from Node 16's punycode.toASCII / encode.
        Punycode.ToAscii("joão.pt").Should().Be("xn--joo-nla.pt");
        Punycode.ToAscii("ação。com").Should().Be("xn--ao-siap.com");
        Punycode.Encode("😀").Should().Be("e28h");
    }

    private static string Escape(string value) =>
        string.Concat(value.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:x4}" : c.ToString()));
}
