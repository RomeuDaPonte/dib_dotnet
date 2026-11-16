using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Domain.Entities;

/// <summary>
/// A client or supplier (legacy <c>models/entity.js</c>, collection <c>entities</c>, PLAN.md §4.2). Values are as
/// stored (never trimmed: production has leading/trailing spaces and a tab in a fiscal number); every field but the
/// id may be missing (<c>null</c>) in an imported document.
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="Name">The entity's name (unique on create, exact match).</param>
/// <param name="FiscalNumber">Contribuinte (NIF), free text.</param>
/// <param name="Address">Morada.</param>
/// <param name="ZipCode">Código postal.</param>
/// <param name="Location">Localidade.</param>
/// <param name="PaymentCondition">The name of a payment method (copied to new budgets; not checked against the list).</param>
/// <param name="Type">One of <see cref="EntityTypes"/> (the schema's enum; not checked on read).</param>
/// <param name="Version">The stored <c>__v</c> (answered by the legacy API).</param>
public sealed record BusinessEntity(
    string Id,
    string? Name,
    string? FiscalNumber,
    string? Address,
    string? ZipCode,
    string? Location,
    string? PaymentCondition,
    string? Type,
    int Version)
{
    /// <summary>Legacy <c>getallclients</c>: type <c>Cliente</c> or <c>Cliente e Fornecedor</c> (exact match).</summary>
    public bool IsClient => Type is EntityTypes.Client or EntityTypes.ClientAndSupplier;
}
