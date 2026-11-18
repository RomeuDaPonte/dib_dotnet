using System.Globalization;
using Dib4t.Client.Forms;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Budgets;

/// <summary>
/// The "Criar obra" dialog (legacy <c>budgets/newJobModal.jsx</c>, PLAN.md §7.3) and its client-side check, the legacy
/// yup <c>newJobSchema</c> (<c>schemas/job/newJobSchema.js</c>) with yup's messages: final price (the budget's total,
/// a number), delivery date (today + the budget's days), initial state (the six states before billing, the first
/// chosen), order identification (required) and the official flag.
/// </summary>
public sealed class JobForm
{
    private string? _finalPrice;
    private bool _finalPriceTyped;

    private JobForm(string budgetId, double? total)
    {
        BudgetId = budgetId;
        Total = total;
        _finalPrice = total is { } value ? JsNumber.ToString(value) : null;
    }

    /// <summary>The initial states of the dialog's list (legacy <c>newJobModal.jsx</c>, in its order).</summary>
    public static IReadOnlyList<string> States { get; } =
        [JobStates.Development, JobStates.OnApproval, JobStates.Approved, JobStates.Machining, JobStates.Assembly, JobStates.ForInvoicing];

    /// <summary>The revision the job is made from.</summary>
    public string BudgetId { get; }

    /// <summary>The budget's total (the initial final price); <c>null</c> in old budgets.</summary>
    public double? Total { get; }

    /// <summary>"Preço final €": the total's text until the user types (the server gets the number until then, as legacy).</summary>
    public string? FinalPrice
    {
        get => _finalPrice;
        set
        {
            _finalPrice = value;
            _finalPriceTyped = true;
        }
    }

    /// <summary>"Data de entrega" as the date input holds it: <c>yyyy-MM-dd</c>, or empty.</summary>
    public string DueDate { get; set; } = string.Empty;

    /// <summary>"Estado inicial".</summary>
    public string State { get; set; } = JobStates.Development;

    /// <summary>"Identificação da encomenda".</summary>
    public string OrderIdentification { get; set; } = string.Empty;

    /// <summary>"Identificação oficial".</summary>
    public bool IsOfficialOrderIdentification { get; set; }

    /// <summary>
    /// The dialog for a budget row: the final price is its total; the delivery date is today in the browser's zone
    /// plus its days (legacy <c>date.setDate(date.getDate() + days)</c>, only when the days are not 0; the legacy
    /// dialog then shows the <b>UTC</b> date of that moment, which is the day before between midnight and 01:00 in
    /// Lisbon's summer: fixed, the local date is shown).
    /// </summary>
    public static JobForm For(string budgetId, double? total, double daysNeededToDoTheWork, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = double.IsFinite(daysNeededToDoTheWork) ? (int)Math.Truncate(daysNeededToDoTheWork) : 0;
        return new JobForm(budgetId, total) { DueDate = today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
    }

    /// <summary>yup, in the schema's order; each field's first failing rule.</summary>
    public JobFormErrors Validate()
    {
        string? finalPrice = null;
        if (_finalPrice is null)
        {
            finalPrice = Yup.Required("finalPrice"); // an old budget without a total, untouched
        }
        else if (Yup.Number(_finalPrice) is null)
        {
            finalPrice = Yup.NumberTypeError("finalPrice", _finalPrice);
        }

        var dueDate = DateOnly.TryParseExact(DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : $"dueDate must be a `date` type, but the final value was: `Invalid Date` (cast from the value `\"{DueDate}\"`).";
        var order = string.IsNullOrEmpty(OrderIdentification) ? Yup.Required("orderIdentification") : null;
        return new JobFormErrors(finalPrice, dueDate, order);
    }

    /// <summary>What the dialog sends (legacy <c>submit</c> + <c>budgetId</c>).</summary>
    public NewJobRequest ToRequest() => new(
        BudgetId,
        _finalPriceTyped || Total is not { } total ? _finalPrice ?? string.Empty : total,
        DueDate,
        State,
        OrderIdentification,
        IsOfficialOrderIdentification);
}

/// <summary>Per-field messages shown under the inputs.</summary>
public sealed record JobFormErrors(string? FinalPrice, string? DueDate, string? OrderIdentification)
{
    public static JobFormErrors None { get; } = new(null, null, null);

    public bool Any => FinalPrice is not null || DueDate is not null || OrderIdentification is not null;
}
