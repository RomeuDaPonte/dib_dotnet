using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Dib4t.Infrastructure.Auth;

/// <summary>
/// Configuration section <c>Jwt</c> (PLAN.md §3.2, §4.6, D12). The signing key is the new app's own (never the legacy
/// one), from user-secrets/appsettings in Development and from the environment (<c>Jwt__Key</c>) in Production.
/// Changing it logs everyone out, which is also the emergency switch if a token is stolen. Validated at startup.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HS256 needs at least 256 bits.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>The prefix of the committed development key; such a key is refused in Production.</summary>
    public const string DevelopmentKeyPrefix = "development-only-";

    /// <summary>The HMAC-SHA256 key: its UTF-8 bytes, at least <see cref="MinimumKeyBytes"/>.</summary>
    [Required]
    public string Key { get; set; } = string.Empty;

    public byte[] KeyBytes() => Encoding.UTF8.GetBytes(Key);
}
