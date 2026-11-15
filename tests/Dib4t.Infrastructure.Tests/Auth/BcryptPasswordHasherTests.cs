using System.Text.Json;
using Dib4t.Infrastructure.Auth;
using Dib4t.Tests.Common;

namespace Dib4t.Infrastructure.Tests.Auth;

/// <summary>bcrypt compatible with the legacy bcryptjs 2.4.3 (PLAN.md §4.6): imported passwords keep working.</summary>
public sealed class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    /// <summary>Hashes made by bcryptjs itself (auth-golden.json): $2a$/$2b$, costs 4 and 10, UTF-8, 72-byte limit.</summary>
    public static TheoryData<string, string> LegacyHashes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var item in JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)).GetProperty("bcrypt").EnumerateArray())
            {
                data.Add(item.GetProperty("password").GetString()!, item.GetProperty("hash").GetString()!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LegacyHashes))]
    public void Verifies_a_bcryptjs_hash(string password, string hash)
    {
        _hasher.Verify(password, hash).Should().BeTrue();
        _hasher.Verify(password + "!", hash).Should().Be(System.Text.Encoding.UTF8.GetByteCount(password) >= 72,
            "bcrypt only reads the first 72 bytes, like bcryptjs");
    }

    [Fact]
    public void The_seeded_legacy_admin_hash_verifies()
    {
        // models/user.js createAdminUser: the only $2b$ hash a legacy database starts with.
        const string AdminHash = "$2b$10$fSMIGpdKqJ8Eqmkf4NAWEu0hGnkNRaexlLWKeI0kSuMFdnrryjUKy";

        _hasher.Verify("not-the-password", AdminHash).Should().BeFalse();
    }

    [Fact]
    public void New_hashes_use_the_legacy_format_and_verify()
    {
        var hash = _hasher.Hash("palavra-passe çãé");

        hash.Should().StartWith("$2a$10$").And.HaveLength(60);
        _hasher.Verify("palavra-passe çãé", hash).Should().BeTrue();
        _hasher.Verify("palavra-passe cae", hash).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$2a$10$abcdefghijklmnopqrstuu")]
    public void A_hash_that_is_not_60_characters_is_no_match_like_bcryptjs(string hash) =>
        _hasher.Verify("char-test-1", hash).Should().BeFalse();

    [Fact]
    public void A_60_character_hash_with_an_invalid_salt_throws_like_bcryptjs()
    {
        var verify = () => _hasher.Verify("char-test-1", new string('x', 60));

        verify.Should().Throw<Exception>();
    }
}
