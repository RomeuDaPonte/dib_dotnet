using System.Globalization;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Calculations;

namespace Dib4t.Domain.Tests.SharedKernel;

/// <summary>
/// The budget details page's live amounts and the JavaScript number functions behind them, against values computed by
/// Node 16 (the legacy runtime) with the legacy formulas of <c>budgetGeneralDetails.jsx</c>, <c>budgetDetails.jsx</c>
/// and <c>budgetDetailsHelpers.js</c> (inputs are texts, as the legacy inputs hold them).
/// </summary>
public sealed class BudgetCalculationsTests
{
    [Theory]
    [InlineData(0.0000000000000000d, "0.00")]
    [InlineData(1.0000000000000000d, "1.00")]
    [InlineData(1.0049999999999999d, "1.00")]
    [InlineData(1.2350000000000001d, "1.24")]
    [InlineData(0.12500000000000000d, "0.13")]
    [InlineData(-0.12500000000000000d, "-0.13")]
    [InlineData(-0.0010000000000000000d, "-0.00")]
    [InlineData(2.5000000000000000d, "2.50")]
    [InlineData(1234.5650000000001d, "1234.57")]
    [InlineData(12345.674999999999d, "12345.67")]
    [InlineData(0.30000000000000004d, "0.30")]
    [InlineData(43.500000000000000d, "43.50")]
    [InlineData(1.0000000000000000e+20d, "100000000000000000000.00")]
    [InlineData(1.0000000000000000e+21d, "1e+21")]
    [InlineData(-1.0000000000000000e+21d, "-1e+21")]
    [InlineData(123456789.12500000d, "123456789.13")]
    [InlineData(4.9406564584124654e-324d, "0.00")]
    [InlineData(9.9999999999999995e-7d, "0.00")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    [InlineData(99.995000000000005d, "100.00")]
    [InlineData(-1.5000000000000000d, "-1.50")]
    [InlineData(1.4500000000000000d, "1.45")]
    [InlineData(8.3450000000000006d, "8.35")]
    [InlineData(4143.7500000000000d, "4143.75")]
    public void ToFixed_2_is_javascript_toFixed(double value, string expected) => JsNumber.ToFixed(value, 2).Should().Be(expected);

    [Theory]
    [InlineData(1.5d, 0, "2")]
    [InlineData(2.5d, 0, "3")]
    [InlineData(-2.5d, 0, "-3")]
    [InlineData(0.5d, 0, "1")]
    public void ToFixed_rounds_ties_away_from_zero(double value, int digits, string expected) => JsNumber.ToFixed(value, digits).Should().Be(expected);

    [Theory]
    [InlineData("", 0d)]
    [InlineData(" ", 0d)]
    [InlineData("12", 12d)]
    [InlineData(" 12 ", 12d)]
    [InlineData("1.5", 1.5d)]
    [InlineData(".5", 0.5d)]
    [InlineData("5.", 5d)]
    [InlineData("-1.5", -1.5d)]
    [InlineData("+3", 3d)]
    [InlineData("1e3", 1000d)]
    [InlineData("1E-2", 0.01d)]
    [InlineData("0x10", 16d)]
    [InlineData("0X1f", 31d)]
    [InlineData("0o17", 15d)]
    [InlineData("0b101", 5d)]
    [InlineData("-0x10", double.NaN)]
    [InlineData("abc", double.NaN)]
    [InlineData("12abc", double.NaN)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    [InlineData("infinity", double.NaN)]
    [InlineData("1e400", double.PositiveInfinity)]
    [InlineData("\t7\n", 7d)]
    [InlineData("1,5", double.NaN)]
    [InlineData("0.1", 0.1d)]
    [InlineData("00012", 12d)]
    [InlineData("1_000", double.NaN)]
    [InlineData(".", double.NaN)]
    public void FromText_is_javascript_Number(string text, double expected) => JsNumber.FromText(text).Should().Be(expected);

    /// <summary>Tasks as <c>quantity*unitPrice;…</c> texts; an empty total is a budget without one (old document).</summary>
    [Theory]
    [InlineData("20*30;15*38.5;10*28;1.5*1250.75", "25", "12345.67", "3333.625", "4166.625", "4167.03", "270.34", "lightGreen")]
    [InlineData("1*32.5", "35", "43.5", "32.5", "43.5", "43.88", "33.83", "lightGreen")]
    [InlineData("1*32.5", "35", "43.50", "32.5", "43.5", "43.88", "33.83", "lightGreen")]
    [InlineData("*32.5", "35", "43.5", "0", "0", "0.00", "Infinity", "lightGreen")]
    [InlineData("abc*32.5", "35", "43.5", "NaN", "NaN", "NaN", "NaN", "yellow")]
    [InlineData("3*100", "30", "", "300", "390", "390.00", "NaN", "yellow")]
    [InlineData("3*100", "30", "350", "300", "390", "390.00", "16.67", "red")]
    [InlineData("3*100", "30", "380", "300", "390", "390.00", "26.67", "lightGreen")]
    [InlineData("3*100", "30", "360", "300", "390", "390.00", "20.00", "yellow")]
    [InlineData("0.1*0.2;0.7*3", "33.3", "3", "2.1199999999999997", "2.1199999999999997", "2.83", "41.32", "lightGreen")]
    [InlineData("2*19.99", "", "40", "39.98", "39.98", "39.98", "NaN", "yellow")]
    public void Live_amounts_follow_the_legacy_formulas(
        string tasks, string margin, string total, string tasksTotal, string totalFromTasks, string withMargin, string effective, string color)
    {
        var lines = tasks.Split(';').Select(t => t.Split('*')).Select(p => (JsNumber.FromText(p[0]), JsNumber.FromText(p[1])));
        var sum = BudgetCalculations.TasksTotal(lines);
        var m = JsNumber.FromText(margin);
        var t = total.Length == 0 ? double.NaN : JsNumber.FromText(total);

        JsNumber.ToString(sum).Should().Be(tasksTotal);
        JsNumber.ToString(BudgetCalculations.TotalFromTasks(sum, m)).Should().Be(totalFromTasks);
        BudgetCalculations.WithMargin(sum, m).Should().Be(withMargin);
        BudgetCalculations.EffectiveMargin(m, sum, t).Should().Be(effective);
        BudgetCalculations.MarginColor(effective).Should().Be(color);
    }

    [Fact]
    public void ToFixed_of_negative_zero_has_no_sign() => JsNumber.ToFixed(-0d, 2).Should().Be("0.00");

    [Theory]
    [InlineData("25.01", "lightGreen")]
    [InlineData("25.00", "yellow")]
    [InlineData("20.00", "yellow")]
    [InlineData("19.99", "red")]
    [InlineData("NaN", "yellow")]
    [InlineData("-Infinity", "red")]
    public void The_margin_color_has_the_legacy_bounds(string margin, string color) =>
        BudgetCalculations.MarginColor(margin).Should().Be(color);

    [Fact]
    public void ToFixed_of_every_cent_matches_decimal_rounding()
    {
        // Exact decimals k/100 written as doubles: toFixed(2) gives the cents back (the double is the nearest value).
        for (var cents = -100_000; cents <= 100_000; cents += 7)
        {
            var value = cents / 100d;
            JsNumber.ToFixed(value, 2).Should().Be((cents / 100m).ToString("0.00", CultureInfo.InvariantCulture), cents.ToString(CultureInfo.InvariantCulture));
        }
    }
}
