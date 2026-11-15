using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Settings;

/// <summary>Legacy <c>routes/prices.js</c>: <c>GET</c> (T) and <c>POST /api/prices</c> (S), PLAN.md §6, §7.2.</summary>
public sealed partial class PricesService(IPricesRepository prices, ILogger<PricesService> logger)
{
    /// <summary>The prices, or <c>null</c> when there is no document (legacy answers <c>{}</c>).</summary>
    public Task<Prices?> GetAsync(CancellationToken cancellationToken) => prices.FindAsync(cancellationToken);

    /// <summary>Joi (400) → set the six values and save. Without a prices document the legacy route throws (500).</summary>
    public async Task<Result<Prices>> UpdateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = SettingsValidators.ValidatePrices(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (!await prices.UpdateAsync(validated.Value, cancellationToken))
        {
            LogNoPrices();
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        var saved = validated.Value;
        LogUpdated(saved.Automation, saved.Consultancy, saved.Development, saved.Machining, saved.Assembly, saved.Margin);
        return saved;
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Information,
        Message = "Prices updated: automation {Automation}, consultancy {Consultancy}, development {Development}, machining {Machining}, assembly {Assembly}, margin {Margin}")]
    private partial void LogUpdated(double? automation, double? consultancy, double? development, double? machining, double? assembly, double? margin);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error, Message = "Prices not updated: there is no prices document")]
    private partial void LogNoPrices();
}
