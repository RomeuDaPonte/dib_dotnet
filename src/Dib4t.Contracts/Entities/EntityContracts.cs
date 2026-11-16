using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Entities;

/// <summary>
/// An entity as the legacy API answers it: the stored document <c>{ _id, name, fiscalNumber, address, zipCode,
/// location, paymentCondition, type, __v }</c> (<c>getAll</c>, <c>getallclients</c>, <c>GET /:id</c>, <c>POST /</c>,
/// <c>search</c>) or, without <c>__v</c>, <c>_.pick</c> of the saved entity (<c>PUT /:id</c>). A missing value is
/// left out.
/// </summary>
public sealed record EntityResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FiscalNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Address,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ZipCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentCondition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonPropertyName("__v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Version);

/// <summary>
/// <c>POST /api/entity</c> and <c>PUT /api/entity/:id</c> body, as the legacy dialog sends it
/// (<c>entityService.js</c>): every field, <c>""</c> when left empty; a value the edited entity does not have
/// (<c>null</c> here) is not sent, so it stays missing.
/// </summary>
public sealed record EntityRequest(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FiscalNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Address,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ZipCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    string PaymentCondition,
    string Type);

/// <summary><c>GET /api/entity/getentityTypes</c>: <c>{ _id: 1, name: "Cliente" }</c>, …</summary>
public sealed record EntityTypeResponse([property: JsonPropertyName("_id")] int Id, string Name);

/// <summary><c>POST /api/entity/search</c> body, as the legacy service sends it (every key; <c>""</c> = any).</summary>
public sealed record EntitySearchRequest(string Name, string Nif, string Localidade, string Morada, string CodigoPostal);
