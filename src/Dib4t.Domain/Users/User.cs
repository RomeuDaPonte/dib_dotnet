namespace Dib4t.Domain.Users;

/// <summary>
/// A user of the app (legacy <c>models/user.js</c>, collection <c>users</c>). Values are as stored: imported documents
/// may lack <see cref="Name"/>, <see cref="Email"/> or <see cref="PasswordHash"/>, and <see cref="Roles"/> may hold
/// <c>null</c> elements (PLAN.md §4.5 item 2).
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="Name">Display name (legacy min. 3 characters).</param>
/// <param name="Email">Login e-mail, matched exactly.</param>
/// <param name="PasswordHash">bcrypt hash (<c>$2a$</c>/<c>$2b$</c>). Never logged or returned.</param>
/// <param name="Roles">The stored <c>roles</c> array (<c>RoleNames</c>), <c>null</c> elements included.</param>
/// <param name="FieldOfCompetencies">The job state the user works in (<c>FieldsOfCompetence</c>).</param>
public sealed record User(
    string Id,
    string? Name,
    string? Email,
    string? PasswordHash,
    IReadOnlyList<string?> Roles,
    string FieldOfCompetencies)
{
    /// <summary>The roles that count for authorization and the menu: the stored ones without <c>null</c> elements.</summary>
    public IEnumerable<string> EffectiveRoles => Roles.OfType<string>();

    /// <summary>Exact, case-sensitive match like the legacy <c>roles.includes(role)</c>; <c>null</c> elements are skipped.</summary>
    public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
}
