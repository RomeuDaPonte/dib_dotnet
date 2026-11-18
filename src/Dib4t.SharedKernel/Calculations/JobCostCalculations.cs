using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.SharedKernel.Calculations;

/// <summary>A budget task as the job cost table reads it (<c>taskType</c>, <c>quantity</c>, <c>unitPrice</c>; NaN for a missing number).</summary>
public readonly record struct CostTask(string? TaskType, double Quantity, double UnitPrice);

/// <summary>
/// The registered values of a job as the details page holds them: stored numbers, or the inputs' texts once typed
/// (<see cref="JsValue"/>). A missing statistic's hours are <see cref="JsValue.Undefined"/>.
/// </summary>
public sealed record JobCostValues(
    JsValue DevelopmentHours,
    JsValue MachiningHours,
    JsValue AssemblyHours,
    JsValue Components,
    JsValue Automation,
    JsValue Materials,
    JsValue Others,
    JsValue CostNonQuality);

/// <summary>An amount with its color class (<see cref="JobCostCalculations.Profit"/> or <see cref="JobCostCalculations.Loss"/>).</summary>
public readonly record struct ColoredText(string Text, string? CssClass);

/// <summary>The cost summary above the table (legacy <c>costsTableHeade.jsx</c>), as printed.</summary>
public sealed record JobCostSummary(
    string TasksTotal,
    string BudgetTotal,
    string Registered,
    ColoredText PreviewedProfit,
    ColoredText Deviation,
    ColoredText TotalProfit,
    ColoredText Margin,
    ColoredText DeviationPercent,
    ColoredText TotalPercent);

/// <summary>
/// One calculated row of the cost table: "Orçamento", the "Registo" input's value (<c>null</c>: legacy hides the
/// input for a negative or invalid value), "Desvio" and "Margem".
/// </summary>
public sealed record JobCostRow(string Label, string Budgeted, JsValue? Field, string Deviation, ColoredText Percent);

/// <summary>The cost section of the job details page: summary and the six calculated rows (materials and non-quality show only their input).</summary>
public sealed record JobCostPage(JobCostSummary Summary, IReadOnlyList<JobCostRow> Rows);

/// <summary>
/// The job details page's cost summary and cost table (legacy <c>front-end/src/_helpers/taskHelper.js</c>,
/// <c>useTaskHoursRowState.js</c>, <c>costsTableHeade.jsx</c>, the <c>*Row.jsx</c> files; PLAN.md §7.3), with JavaScript
/// number semantics in the legacy operation order (checked against <c>job-costs-golden.json</c>: the legacy code run
/// in Node). Prices are the hourly prices of <c>GET /api/prices</c>; the page adds <c>others = components = 1</c>.
/// Fixed legacy defects: a missing statistic counts 0 hours (legacy: the page crashes, or NaN); a row's percentage of
/// a type with nothing budgeted is "-" (legacy means to, but compares a text with <c>-Infinity</c>, so it prints
/// "-Infinity %"); the previewed margin is colored by its sign like the other amounts (legacy: always green).
/// </summary>
public static class JobCostCalculations
{
    public const string Profit = "profit";

    public const string Loss = "loss";

    /// <summary>The page's cost section for the budget's tasks, the job's values, the budget total and the hourly prices.</summary>
    public static JobCostPage Page(IReadOnlyList<CostTask> tasks, JobCostValues job, double budgetTotal, IReadOnlyDictionary<string, double> prices)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(prices);
        var pagePrices = new Dictionary<string, double>(prices, StringComparer.Ordinal) { [TaskTypes.Others] = 1, [TaskTypes.Components] = 1 };
        var values = job with
        {
            DevelopmentHours = job.DevelopmentHours.OrDefault(0),
            MachiningHours = job.MachiningHours.OrDefault(0),
            AssemblyHours = job.AssemblyHours.OrDefault(0),
        };

        // JobResultsProvider: nothing is calculated without tasks (the initial zeros stay)
        var deviationEuros = 0d;
        var deviationPercent = JsValue.Of(0);
        var registered = 0d;
        if (tasks.Count >= 1)
        {
            deviationEuros = TotalDeviation(tasks, values, pagePrices);
            deviationPercent = JsValue.Text(JsNumber.ToFixed(deviationEuros * 100 / TasksTotal(tasks), 0));
            registered = SumOfAllExpenses(tasks, values, pagePrices);
        }

        var tasksTotal = TasksTotal(tasks);
        var margin = JsNumber.ToFixed((budgetTotal - tasksTotal) * 100 / tasksTotal, 0);
        var previewed = budgetTotal - tasksTotal;
        var totalProfit = previewed + deviationEuros;
        var totalPercent = deviationPercent.ToNumber() + JsNumber.FromText(margin);
        var summary = new JobCostSummary(
            Euros(tasksTotal),
            Euros(budgetTotal),
            Euros(registered),
            new(Euros(previewed), Class(previewed)),
            new(Euros(deviationEuros), Class(deviationEuros)),
            new(Euros(totalProfit), Class(totalProfit)),
            new(margin + " %", Class(JsNumber.FromText(margin))),
            new(deviationPercent + " %", Class(deviationPercent.ToNumber())),
            new(JsNumber.ToString(totalPercent) + " %", Class(totalPercent)));

        var totals = Totals(tasks);
        IReadOnlyList<JobCostRow> rows =
        [
            HoursRow("Desenvolvimento", values.DevelopmentHours, TaskTypes.Development, JsValue.Of(0)),
            MoneyRow("Componentes", values.Components, TaskTypes.Components),
            MoneyRow("Automação", values.Automation, TaskTypes.Automation),
            HoursRow("Montagem", values.AssemblyHours, TaskTypes.Assembly, JsValue.Of(0)),
            HoursRow("Maquinação", values.MachiningHours, TaskTypes.Machining, values.Materials.OrDefault(0)),
            MoneyRow("Outros", values.Others, TaskTypes.Others),
        ];
        return new JobCostPage(summary, rows);

        JobCostRow HoursRow(string label, JsValue hours, string type, JsValue moneySpent) =>
            Row(label, totals[type], RowState(tasks, hours, totals[type], type, moneySpent, pagePrices), hours);

        // ComponentsRow / AutomationRow / OthersRow call the row state without prices and with 0 hours
        JobCostRow MoneyRow(string label, JsValue value, string type)
        {
            var spent = value.OrDefault(0);
            return Row(label, totals[type], RowState(tasks, JsValue.Of(0), totals[type], type, spent, null), spent);
        }
    }

    /// <summary><c>calculateTasksTotalWithoutMargin</c>: <c>Σ unitPrice × quantity</c> in the tasks' order.</summary>
    public static double TasksTotal(IReadOnlyList<CostTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var total = 0d;
        foreach (var task in tasks)
        {
            total += task.UnitPrice * task.Quantity;
        }

        return total;
    }

    /// <summary><c>calculateTasksTotals</c>: <c>Σ unitPrice × quantity</c> per row type (consultancy in development).</summary>
    public static IReadOnlyDictionary<string, double> Totals(IReadOnlyList<CostTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var totals = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [TaskTypes.Development] = 0,
            [TaskTypes.Automation] = 0,
            [TaskTypes.Machining] = 0,
            [TaskTypes.Assembly] = 0,
            [TaskTypes.Components] = 0,
            [TaskTypes.Others] = 0,
        };
        foreach (var task in tasks)
        {
            var type = task.TaskType == TaskTypes.Consultancy ? TaskTypes.Development : task.TaskType;
            if (type is not null && totals.ContainsKey(type))
            {
                totals[type] += task.UnitPrice * task.Quantity;
            }
        }

        return totals;
    }

    /// <summary>
    /// <c>calculatePriceForTask(tasks, type, usedHours, prices)</c>: the cost of the used hours at the budget's average
    /// hourly price of the type (<c>(value / hours).toFixed(2)</c>, then <c>Math.floor(average × usedHours)</c>); without
    /// budgeted hours of the type, at the price list's (<c>Math.floor(prices[type] × usedHours)</c>), or 0 without prices.
    /// </summary>
    public static double PriceForTask(IReadOnlyList<CostTask> tasks, string type, JsValue usedHours, IReadOnlyDictionary<string, double>? prices)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var (budgetedHours, budgetedValue) = BudgetedHoursAndValue(tasks, type);
        if (budgetedHours == 0 && prices is null)
        {
            return 0;
        }

        if (budgetedHours == 0)
        {
            return Math.Floor(prices!.GetValueOrDefault(type, double.NaN) * usedHours.ToNumber());
        }

        var average = JsNumber.ToFixed(budgetedValue / budgetedHours, 2);
        return Math.Floor(JsNumber.FromText(average) * usedHours.ToNumber());
    }

    /// <summary><c>calculateDeviationInPercent</c>: 0 for no deviation, else <c>(deviation × 100 / budgeted).toFixed(0)</c>.</summary>
    public static JsValue DeviationInPercent(double deviation, double budgeted) =>
        deviation == 0 ? JsValue.Of(0) : JsValue.Text(JsNumber.ToFixed(deviation * 100 / budgeted, 0));

    /// <summary><c>calculateTotalDeviation</c>: Σ (budgeted − registered cost) of the rows, minus the non-quality cost.</summary>
    public static double TotalDeviation(IReadOnlyList<CostTask> tasks, JobCostValues job, IReadOnlyDictionary<string, double> prices)
    {
        ArgumentNullException.ThrowIfNull(job);
        var totals = Totals(tasks);
        var assembly = totals[TaskTypes.Assembly] - PriceForTask(tasks, TaskTypes.Assembly, job.AssemblyHours, prices);
        var automation = totals[TaskTypes.Automation] - job.Automation.ToNumber();
        var components = totals[TaskTypes.Components] - job.Components.ToNumber();
        var development = totals[TaskTypes.Development] - PriceForTask(tasks, TaskTypes.Development, job.DevelopmentHours, prices);
        var machining = totals[TaskTypes.Machining] - PriceForTask(tasks, TaskTypes.Machining, job.MachiningHours, prices) - job.Materials.ToNumber();
        var others = totals[TaskTypes.Others] - job.Others.ToNumber();
        return assembly + automation + components + development + machining + others - job.CostNonQuality.ToNumber();
    }

    /// <summary><c>getSumOfAllExpenses</c>: the registered costs (hours at their prices, the amounts, the non-quality cost).</summary>
    public static double SumOfAllExpenses(IReadOnlyList<CostTask> tasks, JobCostValues job, IReadOnlyDictionary<string, double> prices)
    {
        ArgumentNullException.ThrowIfNull(job);
        return PriceForTask(tasks, TaskTypes.Assembly, job.AssemblyHours, prices)
            + job.Automation.ToNumber()
            + job.Components.ToNumber()
            + PriceForTask(tasks, TaskTypes.Development, job.DevelopmentHours, prices)
            + PriceForTask(tasks, TaskTypes.Machining, job.MachiningHours, prices)
            + job.Others.ToNumber()
            + job.CostNonQuality.ToNumber();
    }

    /// <summary><c>`${x} €`</c>.</summary>
    public static string Euros(double value) => JsNumber.ToString(value) + " €";

    /// <summary><c>x &gt;= 0 ? "profit" : "loss"</c> (NaN is a loss).</summary>
    public static string Class(double value) => value >= 0 ? Profit : Loss;

    /// <summary>
    /// <c>useTaskHoursRowState</c>'s effect: the registered cost is the hours' price, or the typed amount when it is
    /// truthy (machining: always the hours, minus the materials); deviation = budgeted − cost.
    /// </summary>
    private static (double Deviation, JsValue Percent) RowState(
        IReadOnlyList<CostTask> tasks, JsValue hours, double budgeted, string type, JsValue moneySpent, IReadOnlyDictionary<string, double>? prices)
    {
        var cost = !moneySpent.IsTruthy || type == TaskTypes.Machining ? PriceForTask(tasks, type, hours, prices) : moneySpent.ToNumber();
        var deviation = type == TaskTypes.Machining ? budgeted - cost - moneySpent.ToNumber() : budgeted - cost;
        return (deviation, DeviationInPercent(deviation, budgeted));
    }

    private static JobCostRow Row(string label, double budgeted, (double Deviation, JsValue Percent) state, JsValue field)
    {
        var percent = double.IsInfinity(state.Percent.ToNumber())
            ? new ColoredText("-", null)
            : new ColoredText(state.Percent + " %", Class(state.Deviation));
        return new JobCostRow(label, Euros(budgeted), field.ToNumber() >= 0 ? field : null, Euros(state.Deviation), percent);
    }

    private static (double Hours, double Value) BudgetedHoursAndValue(IReadOnlyList<CostTask> tasks, string type)
    {
        double hours = 0, value = 0;
        foreach (var task in tasks)
        {
            if (task.TaskType == type)
            {
                hours += task.Quantity;
                value += task.UnitPrice * task.Quantity;
            }

            if (task.TaskType == TaskTypes.Consultancy && type == TaskTypes.Development)
            {
                hours += task.Quantity;
                value += task.UnitPrice * task.Quantity;
            }
        }

        return (hours, value);
    }
}
