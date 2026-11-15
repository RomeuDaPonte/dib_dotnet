using Dib4t.Application.Settings;
using Dib4t.Contracts.Settings;
using Dib4t.Domain.Settings;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/entity</c> (legacy <c>routes/entity.js</c>, PLAN.md §6), every route T. This step: the payment methods
/// (Settings → M/Pagamento). The entity routes come with the entities slice.
/// </summary>
[Route("api/entity")]
[Authorize]
public sealed class EntityController(PaymentMethodsService paymentMethods) : LegacyControllerBase
{
    [HttpGet("getAllPaymentConditions")]
    public async Task<IActionResult> GetAllPaymentConditions(CancellationToken cancellationToken)
    {
        var methods = await paymentMethods.ListAsync(cancellationToken);
        return Ok(methods.Select(ToResponse));
    }

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
}
