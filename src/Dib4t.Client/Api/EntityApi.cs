using Dib4t.Contracts.Entities;

namespace Dib4t.Client.Api;

/// <summary>The entity routes of <c>/api/entity</c> (PLAN.md §6, §8.2); the payment methods are <see cref="IPaymentMethodsApi"/>.</summary>
public interface IEntityApi
{
    /// <summary><c>GET getall</c>: every entity, sorted by name.</summary>
    Task<ApiResult<IReadOnlyList<EntityResponse>>> ListAsync();

    Task<ApiResult<EntityResponse>> CreateAsync(EntityRequest entity);

    Task<ApiResult<EntityResponse>> UpdateAsync(string id, EntityRequest entity);
}

/// <inheritdoc cref="IEntityApi"/>
public sealed class EntityApi(HttpClient http) : IEntityApi
{
    public Task<ApiResult<IReadOnlyList<EntityResponse>>> ListAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<EntityResponse>>(http, HttpMethod.Get, "api/entity/getall");

    public Task<ApiResult<EntityResponse>> CreateAsync(EntityRequest entity) =>
        ApiCalls.SendAsync<EntityResponse>(http, HttpMethod.Post, "api/entity", entity);

    public Task<ApiResult<EntityResponse>> UpdateAsync(string id, EntityRequest entity) =>
        ApiCalls.SendAsync<EntityResponse>(http, HttpMethod.Put, "api/entity/" + Uri.EscapeDataString(id), entity);
}
