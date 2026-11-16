using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Domain.Entities;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Entities;

/// <summary>
/// The entity routes of legacy <c>routes/entity.js</c> (every route T: any signed-in user, as legacy; PLAN.md §6,
/// §7.2): list, clients, types, one entity, create, edit, search. Same order of checks and texts as the legacy
/// routes; a failure the legacy app leaves to Express (cast error, refused save, invalid pattern) is a 500
/// <see cref="LegacyMessages.SomethingFailed"/> (D16), with the reason in the log only.
/// </summary>
public sealed partial class EntitiesService(IEntityRepository entities, ILogger<EntitiesService> logger)
{
    /// <summary><c>GET getentityTypes</c>: legacy <c>getAllEntityTypes()</c>, ids 1–3.</summary>
    public static IReadOnlyList<(int Id, string Name)> Types { get; } = [.. EntityTypes.All.Select((name, index) => (index + 1, name))];

    /// <summary><c>GET getAll</c>.</summary>
    public Task<IReadOnlyList<BusinessEntity>> ListAsync(CancellationToken cancellationToken) => entities.ListByNameAsync(cancellationToken);

    /// <summary><c>GET getallclients</c> (the client lists of the budget dialogs).</summary>
    public Task<IReadOnlyList<BusinessEntity>> ListClientsAsync(CancellationToken cancellationToken) => entities.ListClientsAsync(cancellationToken);

    /// <summary><c>GET /:id</c>: id cast (500) → unknown (400 <see cref="EntityMessages.NotFound"/>, legacy status).</summary>
    public async Task<Result<BusinessEntity>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (CastId(id) is not { } objectId)
        {
            return CastFailure(id);
        }

        var entity = await entities.FindByIdAsync(objectId, cancellationToken);
        return entity is null ? Error.Validation(EntityMessages.NotFound) : entity;
    }

    /// <summary>
    /// <c>POST /</c>: Joi (400) → name taken (400 <see cref="EntityMessages.NameTaken"/>) → model casts (500) →
    /// insert. Answers the stored entity.
    /// </summary>
    public async Task<Result<BusinessEntity>> CreateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = EntityValidators.Validate(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var name = validated.Value.Name;
        if (await entities.ExistsByNameAsync(name, cancellationToken))
        {
            LogRejected(name, EntityMessages.NameTaken);
            return Error.Validation(EntityMessages.NameTaken);
        }

        var input = validated.Value.ToInput();
        if (!input.IsSuccess)
        {
            LogNotSaved("create", name, input.Error.Reason);
            return input.Error;
        }

        var entity = await entities.InsertAsync(input.Value, cancellationToken);
        LogSaved(entity.Id, "created", entity.Name, entity.Type);
        return entity;
    }

    /// <summary>
    /// <c>PUT /:id</c>: Joi (400) → id cast (500) → unknown (400 <see cref="EntityMessages.Missing"/>) → model casts
    /// (500) → save of what changed. Answers the entity as saved. The name is <b>not</b> checked for duplicates
    /// (legacy: only on create).
    /// </summary>
    public async Task<Result<BusinessEntity>> UpdateAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = EntityValidators.Validate(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (CastId(id) is not { } objectId)
        {
            return CastFailure(id);
        }

        if (await entities.FindByIdAsync(objectId, cancellationToken) is null)
        {
            return Error.Validation(EntityMessages.Missing);
        }

        var input = validated.Value.ToInput();
        if (!input.IsSuccess)
        {
            LogNotSaved("edit", objectId, input.Error.Reason);
            return input.Error;
        }

        var saved = await entities.UpdateAsync(objectId, input.Value, cancellationToken);
        if (saved is null)
        {
            return Error.Validation(EntityMessages.Missing); // deleted meanwhile
        }

        LogSaved(saved.Id, "updated", saved.Name, saved.Type);
        return saved;
    }

    /// <summary><c>POST /search</c> (see <see cref="EntitySearch"/>).</summary>
    public async Task<Result<IReadOnlyList<BusinessEntity>>> SearchAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var search = EntitySearch.From(body);
        if (!search.IsSuccess)
        {
            LogSearchFailed(search.Error.Reason);
            return search.Error;
        }

        var found = await entities.SearchAsync(search.Value, cancellationToken);
        if (found is null)
        {
            LogSearchFailed("MongoDB refused a pattern: " + string.Join(", ", search.Value.Patterns.Select(p => $"{p.Field} /{p.Pattern}/i")));
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        return Result<IReadOnlyList<BusinessEntity>>.Success(found);
    }

    private static string? CastId(string id) => LegacyObjectId.Cast(id);

    private Error CastFailure(string id)
    {
        LogCastFailed(id);
        return Error.Failure(LegacyMessages.SomethingFailed);
    }

    [LoggerMessage(EventId = 60, Level = LogLevel.Information, Message = "Entity {EntityId} {Action}: {Name} ({EntityType})")]
    private partial void LogSaved(string entityId, string action, string? name, string? entityType);

    [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Entity not created ({Name}): {Reason}")]
    private partial void LogRejected(string name, string reason);

    [LoggerMessage(EventId = 62, Level = LogLevel.Warning, Message = "Entity {Action} not saved ({Subject}): {Reason}")]
    private partial void LogNotSaved(string action, string subject, string? reason);

    [LoggerMessage(EventId = 63, Level = LogLevel.Warning, Message = "Not an entity id: {Id} (legacy cast error, 500)")]
    private partial void LogCastFailed(string id);

    [LoggerMessage(EventId = 64, Level = LogLevel.Warning, Message = "Entity search failed (legacy 500): {Reason}")]
    private partial void LogSearchFailed(string? reason);
}
