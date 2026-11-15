namespace Dib4t.Domain.Settings;

/// <summary>
/// The hour prices and the default margin (legacy <c>models/prices.js</c>, the single document of <c>prices</c>).
/// Every value is required by the legacy schema; <c>null</c> only for a field an imported document lacks.
/// </summary>
/// <param name="Automation">Hour price of automation tasks (€).</param>
/// <param name="Consultancy">Hour price of consultancy tasks (€).</param>
/// <param name="Development">Hour price of development tasks (€).</param>
/// <param name="Machining">Hour price of machining tasks (€).</param>
/// <param name="Assembly">Hour price of assembly tasks (€).</param>
/// <param name="Margin">Default budget margin in percent, 1–100.</param>
public sealed record Prices(
    double? Automation,
    double? Consultancy,
    double? Development,
    double? Machining,
    double? Assembly,
    double? Margin)
{
    /// <summary>Legacy <c>createDefaultPrices</c>: every price 10, margin 35.</summary>
    public static Prices Defaults { get; } = new(10, 10, 10, 10, 10, 35);
}
