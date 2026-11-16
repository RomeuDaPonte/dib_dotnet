using System.Globalization;

namespace Dib4t.Domain.Budgets;

/// <summary>
/// Budget numbering (legacy <c>models/budget.js</c> <c>getBudgetNumber</c>, PLAN.md §7.1): the next number of a
/// year is <c>count + 1</c>, where <c>count</c> is the number of distinct budget numbers with a revision dated in
/// that year (from 1 January 00:00 UTC), written <c>YY-NNNN</c>: zero-padded to four digits, not padded from 1000
/// on (<c>26-1000</c>, <c>26-10000</c>). Checked against the legacy code (<c>budget-golden.json</c>).
/// </summary>
public static class BudgetNumber
{
    /// <summary>The number that follows <paramref name="countThisYear"/> numbers in <paramref name="year"/>.</summary>
    public static string Next(int year, int countThisYear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(countThisYear);
        return Format(year, countThisYear + 1);
    }

    /// <summary><c>YY-NNNN</c> for the <paramref name="sequence"/>-th number of <paramref name="year"/>.</summary>
    public static string Format(int year, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        // legacy: new Date().getFullYear().toString().substr(2, 2)
        var yy = year.ToString(CultureInfo.InvariantCulture).Substring(2, 2);
        return $"{yy}-{sequence.ToString("D4", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The start of the counting period of <paramref name="year"/>: legacy <c>new Date("{year}")</c>, 1 January 00:00 UTC.</summary>
    public static DateTime YearStart(int year) => new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
