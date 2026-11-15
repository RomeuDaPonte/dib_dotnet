using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Auth;

/// <summary>
/// The login token exactly as the legacy <b>jsonwebtoken 8.5.1</b> <c>sign(payload, key)</c> writes it (PLAN.md §4.6):
/// header <c>{"alg":"HS256","typ":"JWT"}</c>, payload <c>{_id, name, email, roles, fieldOfCompetencies, iat}</c> in
/// that order (missing name/email left out, <c>null</c> roles kept), <c>JSON.stringify</c> escaping, no <c>exp</c>.
/// Byte-for-byte equal to the legacy library's output for the same key and time (golden file). The token is
/// validated by the standard JwtBearer handler (Server).
/// </summary>
internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
{
    private static readonly string Header = Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);

    public string CreateToken(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var payload = new StringBuilder("{\"_id\":");
        AppendJsonString(payload, user.Id);
        if (user.Name is not null)
        {
            payload.Append(",\"name\":");
            AppendJsonString(payload, user.Name);
        }

        if (user.Email is not null)
        {
            payload.Append(",\"email\":");
            AppendJsonString(payload, user.Email);
        }

        payload.Append(",\"roles\":[");
        for (var i = 0; i < user.Roles.Count; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            if (user.Roles[i] is { } role)
            {
                AppendJsonString(payload, role);
            }
            else
            {
                payload.Append("null");
            }
        }

        payload.Append("],\"fieldOfCompetencies\":");
        AppendJsonString(payload, user.FieldOfCompetencies);
        payload.Append(",\"iat\":")
            .Append(time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append('}');

        var signingInput = Header + "." + Encode(Encoding.UTF8.GetBytes(payload.ToString()));
        var signature = HMACSHA256.HashData(options.Value.KeyBytes(), Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + Encode(signature);
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>
    /// <c>JSON.stringify</c> of a string (ES2019 well-formed): <c>"</c> <c>\</c> and the control characters escaped
    /// (<c>\b \t \n \f \r</c>, else <c>\u00xx</c>), lone surrogates as <c>\udxxx</c>, everything else as it is.
    /// </summary>
    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        builder.Append(c).Append(value[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
