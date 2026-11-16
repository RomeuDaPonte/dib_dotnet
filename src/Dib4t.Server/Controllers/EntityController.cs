using Dib4t.Application.Entities;
using Dib4t.Application.Settings;
using Dib4t.Contracts.Entities;
using Dib4t.Contracts.Settings;
using Dib4t.Domain.Entities;
using Dib4t.Domain.Settings;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/entity</c> (legacy <c>routes/entity.js</c>, PLAN.md §6), every route T (any signed-in user; legacy checks
/// no role here): entities (list, clients, types, one, create, edit, search) and the payment methods (Settings →
/// M/Pagamento). The literal routes win over <c>{id}</c>, as their earlier declaration does in Express.
/// </summary>
[Route("api/entity")]
[Authorize]
public sealed class EntityController(EntitiesService entities, PaymentMethodsService paymentMethods) : LegacyControllerBase
{
    [HttpGet("getAllPaymentConditions")]
    public async Task<IActionResult> GetAllPaymentConditions(CancellationToken cancellationToken)
    {
        var methods = await paymentMethods.ListAsync(cancellationToken);
        return Ok(methods.Select(ToResponse));
    }

    [HttpGet("getAll")]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok((await entities.ListAsync(cancellationToken)).Select(ToResponse));

    [HttpGet("getallclients")]
    public async Task<IActionResult> GetAllClients(CancellationToken cancellationToken) =>
        Ok((await entities.ListClientsAsync(cancellationToken)).Select(ToResponse));

    [HttpGet("getentityTypes")]
    public IActionResult GetEntityTypes() => Ok(EntitiesService.Types.Select(t => new EntityTypeResponse(t.Id, t.Name)));

    [HttpPost("")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await entities.CreateAsync(body, cancellationToken), ToResponse);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await entities.UpdateAsync(id, body, cancellationToken), entity => ToResponse(entity) with { Version = null });
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await entities.SearchAsync(body, cancellationToken), found => found.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Answer(await entities.GetAsync(id, cancellationToken), ToResponse);

    [HttpPost("addPaymentMethod")]
    public async Task<IActionResult> AddPaymentMethod(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await paymentMethods.AddAsync(body, cancellationToken), ToResponse);
    }

    [HttpDelete("deletePaymentMethod/{paymentMethodId}")]
    public async Task<IActionResult> DeletePaymentMethod(string paymentMethodId, CancellationToken cancellationToken) =>
        Answer(await paymentMethods.DeleteAsync(paymentMethodId, cancellationToken), ToResponse);

    private static PaymentMethodResponse ToResponse(PaymentMethod method) => new(method.Id, method.Name, method.Version);

    private static EntityResponse ToResponse(BusinessEntity entity) => new(entity.Id, entity.Name, entity.FiscalNumber, entity.Address,
        entity.ZipCode, entity.Location, entity.PaymentCondition, entity.Type, entity.Version);
}
