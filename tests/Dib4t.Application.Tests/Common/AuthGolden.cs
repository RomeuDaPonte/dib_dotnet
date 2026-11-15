using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.Application.Tests.Common;

/// <summary><c>tests/fixtures/legacy/auth/auth-golden.json</c>: what the legacy code answers (tools/legacy/auth-golden.js).</summary>
internal static class AuthGolden
{
    private static readonly Lazy<JsonElement> Root = new(() => JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)));

    /// <summary>Raw JSON login bodies and the legacy Joi message (<c>null</c> = valid).</summary>
    public static IEnumerable<(string Body, string? Message)> LoginValidation =>
        Root.Value.GetProperty("loginValidation").EnumerateArray()
            .Select(c => (c.GetProperty("body").GetString()!, c.GetProperty("message").GetString()));

    /// <summary>E-mails and whether Joi 14 <c>string().email()</c> (isemail 3.2.0) accepts them.</summary>
    public static IEnumerable<(string Email, bool Valid)> Emails =>
        Root.Value.GetProperty("email").EnumerateArray()
            .Select(c => (c.GetProperty("email").GetString()!, c.GetProperty("valid").GetBoolean()));
}
