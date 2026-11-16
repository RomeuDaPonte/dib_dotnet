namespace Dib4t.Domain.Budgets;

/// <summary>Budget amounts the server computes (PLAN.md §7.1), with JavaScript number arithmetic (IEEE doubles).</summary>
public static class BudgetTotals
{
    /// <summary>
    /// The total of a new budget (legacy <c>POST /api/budget</c>): one automation hour with the default margin,
    /// <c>Math.trunc(margin * automation / 100) + automation</c>.
    /// </summary>
    public static double NewBudgetTotal(double margin, double automationPrice) =>
        Math.Truncate(margin * automationPrice / 100) + automationPrice;
}
