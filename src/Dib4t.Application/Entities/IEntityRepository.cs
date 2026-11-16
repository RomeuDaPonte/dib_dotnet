using Dib4t.Domain.Entities;

namespace Dib4t.Application.Entities;

/// <summary>The <c>entities</c> collection (legacy <c>repositories/entityRepository.js</c> and <c>routes/entity.js</c>).</summary>
public interface IEntityRepository
{
    /// <summary>Legacy <c>getAll</c>: <c>find().sort("name")</c> (MongoDB order: binary, entities without a name first).</summary>
    Task<IReadOnlyList<BusinessEntity>> ListByNameAsync(CancellationToken cancellationToken);

    /// <summary>Legacy <c>getallclients</c>: type <c>Cliente</c> or <c>Cliente e Fornecedor</c>, sorted by name.</summary>
    Task<IReadOnlyList<BusinessEntity>> ListClientsAsync(CancellationToken cancellationToken);

    /// <summary>The entity with this <c>_id</c> (24 lowercase hex digits), or <c>null</c>.</summary>
    Task<BusinessEntity?> FindByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>Legacy <c>findByName</c>: <c>findOne({ name })</c>, exact and case-sensitive.</summary>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts <c>{ _id, name, fiscalNumber?, address?, zipCode?, location?, paymentCondition, type, __v: 0 }</c>
    /// (missing texts left out, <c>null</c> kept); returns the stored entity.
    /// </summary>
    Task<BusinessEntity> InsertAsync(EntityInput entity, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>entity.set({ name, fiscalNumber, address, zipCode, location, paymentCondition, type })</c> +
    /// <c>save()</c>: <c>$set</c> of the values that differ, <c>$unset</c> of the stored texts the body did not send,
    /// nothing when nothing differs (PLAN.md §4.3 rules 3–4). The entity as saved, or <c>null</c> when it does not exist.
    /// </summary>
    Task<BusinessEntity?> UpdateAsync(string id, EntityInput entity, CancellationToken cancellationToken);

    /// <summary>
    /// The entities matching every pattern (case-insensitive, evaluated by MongoDB) in natural order, or <c>null</c>
    /// when MongoDB refuses a pattern (invalid regular expression).
    /// </summary>
    Task<IReadOnlyList<BusinessEntity>?> SearchAsync(EntitySearch search, CancellationToken cancellationToken);
}
