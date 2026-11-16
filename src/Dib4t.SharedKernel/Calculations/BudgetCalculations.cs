namespace Dib4t.SharedKernel.Calculations;

/// <summary>
/// The live amounts of the budget details page (legacy <c>budgetDetails/budgetDetails.jsx</c>,
/// <c>budgetGeneralDetails.jsx</c>, <c>budgetDetailsHelpers.js</c>; PLAN.md §7.1), with JavaScript number
/// semantics: the inputs are texts coerced by <c>Number()</c> (<see cref="JsNumber.FromText"/>), the arithmetic is
/// IEEE doubles in the legacy operation order, so an empty or invalid input gives the same <c>0</c> / <c>NaN</c> the
/// legacy page shows.
/// </summary>
public static class BudgetCalculations
{
    /// <summary>The effective margin is shown in this color above 25 %.</summary>
    public const string GoodMarginColor = "lightGreen";

    /// <summary>... in this color below 20 %.</summary>
    public const string LowMarginColor = "red";

    /// <summary>... and in this color otherwise (also for <c>NaN</c>).</summary>
    public const string MidMarginColor = "yellow";

    /// <summary><c>calculateTasksTotal</c>: <c>Σ quantity × unitPrice</c>, summed from 0 in the list's order.</summary>
    public static double TasksTotal(IEnumerable<(double Quantity, double UnitPrice)> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var total = 0d;
        foreach (var (quantity, unitPrice) in tasks)
        {
            total += quantity * unitPrice;
        }

        return total;
    }

    /// <summary>
    /// <c>updateBudgetTotal</c> (after any task change): <c>tasksTotal + Math.trunc(tasksTotal * margin / 100)</c>, the
    /// total the page then saves with <c>patchProperty</c>.
    /// </summary>
    public static double TotalFromTasks(double tasksTotal, double margin) => tasksTotal + Math.Truncate(tasksTotal * margin / 100);

    /// <summary>
    /// <c>calculateTotalWithPredefinedMargin</c> ("C / Margem", and the total a margin change sets):
    /// <c>(tasksTotal + tasksTotal * margin / 100).toFixed(2)</c>.
    /// </summary>
    public static string WithMargin(double tasksTotal, double margin) => JsNumber.ToFixed(tasksTotal + (tasksTotal * margin / 100), 2);

    /// <summary>
    /// <c>calculateBudgetMargin</c>: the margin the saved total really gives, as text with 2 decimals
    /// (<c>margin − margin · (withMargin − total) / (margin / 100 · tasksTotal)</c>; <c>NaN</c> when the tasks total is 0).
    /// </summary>
    public static string EffectiveMargin(double margin, double tasksTotal, double total)
    {
        var discountInEuros = margin / 100 * tasksTotal;
        var discountInPercent = margin * (JsNumber.FromText(WithMargin(tasksTotal, margin)) - total) / discountInEuros;
        return JsNumber.ToFixed(margin - discountInPercent, 2);
    }

    /// <summary><c>getMarginColor</c> of the effective margin's text.</summary>
    public static string MarginColor(string effectiveMargin)
    {
        var margin = JsNumber.FromText(effectiveMargin);
        if (margin > 25)
        {
            return GoodMarginColor;
        }

        return margin < 20 ? LowMarginColor : MidMarginColor;
    }
}
