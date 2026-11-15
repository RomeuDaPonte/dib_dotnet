using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Settings;

/// <summary>
/// The payment-method routes of legacy <c>routes/entity.js</c> (T: any signed-in user, as legacy):
/// <c>GET getAllPaymentConditions</c>, <c>POST addPaymentMethod</c>, <c>DELETE deletePaymentMethod/:id</c>.
/// </summary>
public sealed partial class PaymentMethodsService(IPaymentMethodRepository methods, ILogger<PaymentMethodsService> logger)
{
    public Task<IReadOnlyList<PaymentMethod>> ListAsync(CancellationToken cancellationToken) => methods.ListAsync(cancellationToken);

    /// <summary>Joi (400) → name taken (400 <see cref="SettingsMessages.PaymentMethodTaken"/>) → insert.</summary>
    public async Task<Result<PaymentMethod>> AddAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = SettingsValidators.ValidatePaymentMethod(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (await methods.ExistsAsync(validated.Value, cancellationToken))
        {
            return Error.Validation(SettingsMessages.PaymentMethodTaken);
        }

        var method = await methods.InsertAsync(validated.Value, cancellationToken);
        LogAdded(method.Id, method.Name);
        return method;
    }

    /// <summary>
    /// id cast (500) → unknown (400 <see cref="SettingsMessages.PaymentMethodMissing"/>, legacy status) → delete →
    /// nothing deleted (500 <see cref="LegacyMessages.ServerError"/>) → answers the deleted method.
    /// </summary>
    public async Task<Result<PaymentMethod>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (LegacyObjectId.Cast(id) is not { } objectId)
        {
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        var method = await methods.FindByIdAsync(objectId, cancellationToken);
        if (method is null)
        {
            return Error.Validation(SettingsMessages.PaymentMethodMissing);
        }

        if (await methods.DeleteAsync(objectId, cancellationToken) is null)
        {
            return Error.Failure(LegacyMessages.ServerError);
        }

        LogDeleted(method.Id, method.Name);
        return method;
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "Payment method {PaymentMethodId} added: {Name}")]
    private partial void LogAdded(string paymentMethodId, string? name);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "Payment method {PaymentMethodId} deleted: {Name}")]
    private partial void LogDeleted(string paymentMethodId, string? name);
}
