using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Account;

/// <summary>
/// <c>GET /api/account/me</c> (new-app only, PLAN.md §4.6, §6): the signed-in user as stored now, in the shape of the
/// legacy token claims <c>{ _id, name, email, roles, fieldOfCompetencies }</c>. <see cref="Roles"/> is the stored
/// array, so it may hold <c>null</c> elements (imported data).
/// </summary>
public sealed record CurrentUserResponse(
    [property: JsonPropertyName("_id")] string Id,
    string? Name,
    string? Email,
    IReadOnlyList<string?> Roles,
    string FieldOfCompetencies);
