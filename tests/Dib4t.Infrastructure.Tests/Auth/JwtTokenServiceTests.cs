using System.Text;
using System.Text.Json;
using Dib4t.Domain.Users;
using Dib4t.Infrastructure.Auth;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dib4t.Infrastructure.Tests.Auth;

/// <summary>The login token is byte for byte what the legacy jsonwebtoken 8.5.1 signs (auth-golden.json, test key).</summary>
public sealed class JwtTokenServiceTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)).GetProperty("jwt");

    public static TheoryData<int> Cases => [.. Enumerable.Range(0, Golden.GetProperty("cases").GetArrayLength())];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Token_equals_the_legacy_library_output(int index)
    {
        var item = Golden.GetProperty("cases")[index];
        var payload = item.GetProperty("payload");
        var user = new User(
            payload.GetProperty("_id").GetString()!,
            payload.TryGetProperty("name", out var name) ? name.GetString() : null,
            payload.TryGetProperty("email", out var email) ? email.GetString() : null,
            "$2a$10$never-in-the-token",
            [.. payload.GetProperty("roles").EnumerateArray().Select(r => r.GetString())],
            payload.GetProperty("fieldOfCompetencies").GetString()!);

        var token = Service(Golden.GetProperty("key").GetString()!, Golden.GetProperty("iat").GetInt64()).CreateToken(user);

        token.Should().Be(item.GetProperty("token").GetString());
    }

    [Fact]
    public void Token_has_no_expiry_and_no_password_hash()
    {
        var user = new User("5f1e9a2b3c4d5e6f70819203", "Ana", "ana@dib4t.local", "$2a$10$secret-hash", [], "Desenvolvimento");

        var token = Service(new string('k', 32), 1_794_650_400).CreateToken(user);
        var payload = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1]));

        payload.Should().Be("""{"_id":"5f1e9a2b3c4d5e6f70819203","name":"Ana","email":"ana@dib4t.local","roles":[],"fieldOfCompetencies":"Desenvolvimento","iat":1794650400}""");
    }

    [Fact]
    public void Strings_are_escaped_like_JSON_stringify()
    {
        var user = new User("id", "a\"b\\c\n\u0001\u001f\ud800é😀<>&\u2028", null, null, [], "x");

        var token = Service(new string('k', 32), 0).CreateToken(user);
        var payload = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1]));

        payload.Should().Contain("\"name\":\"a\\\"b\\\\c\\n\\u0001\\u001f\\ud800é😀<>&\u2028\"");
    }

    private static JwtTokenService Service(string key, long unixSeconds) => new(
        Options.Create(new JwtOptions { Key = key }),
        new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
}
