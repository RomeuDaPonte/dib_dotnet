using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Account;

/// <summary>
/// One row of <c>GET /api/account/users</c>, in the legacy key order (Mongoose puts the defaulted paths first):
/// <c>{ roles, fieldOfCompetencies, _id, name, email }</c>. A missing name or e-mail is left out, as legacy.
/// <see cref="Roles"/> may hold <c>null</c> elements (the legacy UI writes one per unchecked role).
/// </summary>
public sealed record UserListItemResponse(
    IReadOnlyList<string?> Roles,
    string FieldOfCompetencies,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email);

/// <summary>
/// Legacy <c>_.pick(user, ["_id", "name", "email", "roles"])</c>: the answer of <c>GET</c>, <c>PUT</c> and
/// <c>DELETE /api/account/:id</c> and of <c>POST /api/account/create</c>. Missing values are left out.
/// </summary>
public sealed record UserResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string?>? Roles);

/// <summary>
/// The body the user dialog sends to <c>POST /api/account/create</c> and <c>PUT /api/account/:id</c>, as the legacy
/// UI builds it (<c>userModal.jsx</c> <c>buildUser</c>): <see cref="Roles"/> has one slot per role checkbox, in the
/// dialog's order, <c>null</c> when unchecked; on edit an empty <see cref="Password"/> means "unchanged".
/// </summary>
public sealed record UserRequest(
    string Name,
    string Email,
    string Password,
    IReadOnlyList<string?> Roles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FieldOfCompetencies)
{
    /// <summary>Never print the password.</summary>
    public override string ToString() => $"UserRequest {{ Name = {Name}, Email = {Email} }}";
}
