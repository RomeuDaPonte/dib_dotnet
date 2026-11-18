using Dib4t.Domain.Budgets;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Domain.Jobs;

/// <summary>
/// A job ("obra", legacy <c>models/job.js</c>, collection <c>jobs</c>, PLAN.md §4.2, §7.3): made from one budget
/// revision, it follows the production states of <see cref="JobStates"/>. Values are as stored, with the Mongoose
/// defaults for fields an older document lacks (§4.4).
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="InvoicedPercent">Billed share in percent ("Ft %"); missing only after a statistics save without it (legacy unsets it).</param>
/// <param name="FinalPrice">The agreed price (€).</param>
/// <param name="State">One of <see cref="JobStates"/>.</param>
/// <param name="OrderIdentification">The client's order reference ("Por preencher" by default).</param>
/// <param name="IsOfficialOrderIdentification">The order reference is the official one.</param>
/// <param name="Components">Registered cost of components (€).</param>
/// <param name="Automation">Registered cost of automation (€).</param>
/// <param name="Materials">Registered cost of materials (€).</param>
/// <param name="Others">Registered other costs (€).</param>
/// <param name="CostNonQuality">Registered non-quality cost (€); <c>null</c> in one production job.</param>
/// <param name="CommentType">Comment categories (<c>CommentTypes</c>).</param>
/// <param name="BudgetId">→ <c>budgets._id</c>: the revision the job was made from.</param>
/// <param name="CreatedBy">The name of the user who created it.</param>
/// <param name="DueDate">"Finalizar até" (UTC).</param>
/// <param name="StartDate">Creation time (UTC).</param>
/// <param name="DevStatistic">Development hours (also used in approval).</param>
/// <param name="MachiningStatistic">Machining hours and checks.</param>
/// <param name="AssemblyStatistic">Assembly hours and checks.</param>
/// <param name="Priority">One of <see cref="JobPriorities"/>, or missing (old jobs).</param>
/// <param name="Version">Mongoose <c>__v</c>.</param>
public sealed record Job(
    string Id,
    Stored<double> InvoicedPercent,
    double FinalPrice,
    string State,
    string OrderIdentification,
    bool IsOfficialOrderIdentification,
    double Components,
    double Automation,
    double Materials,
    double Others,
    double? CostNonQuality,
    IReadOnlyList<string?> CommentType,
    string? BudgetId,
    string? CreatedBy,
    DateTime? DueDate,
    DateTime? StartDate,
    JobHours? DevStatistic,
    JobMachiningStatistic? MachiningStatistic,
    JobAssemblyStatistic? AssemblyStatistic,
    string? Priority,
    int Version)
{
    /// <summary>Free text of the comments dialog; missing in 139 old jobs, empty in most.</summary>
    public Stored<string> Comments { get; init; }

    /// <summary>When each state ended; missing in old jobs until their first statistics save.</summary>
    public JobStatisticsDates? StatisticsDates { get; init; }

    /// <summary>"Data de entrega"; stored <c>null</c> in many imported jobs, missing in others.</summary>
    public Stored<DateTime> DeliveryDate { get; init; }

    /// <summary>The name of the user who billed it.</summary>
    public string? InvoicedBy { get; init; }

    /// <summary>Stored fields the legacy schema does not know (e.g. <c>isHightPriority</c> of old jobs), as JSON values.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Extra { get; init; } = [];
}

/// <summary><c>jobStatisticsDates</c>: when each state ended (a date can be missing or stored <c>null</c>).</summary>
public sealed record JobStatisticsDates(
    string Id,
    Stored<DateTime> DevEndedInDate,
    Stored<DateTime> OnApprovalEndedInDate,
    Stored<DateTime> ApprovedEndedInDate,
    Stored<DateTime> MachiningEndedInDate,
    Stored<DateTime> AssemblyEndedInDate,
    Stored<DateTime> ForInvoicingEndedInDate)
{
    /// <summary>The element names in the legacy schema order.</summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        "devEndedInDate", "onApprovalEndedInDate", "approvedEndedInDate", "machiningEndedInDate", "assemblyEndedInDate", "forInvoicingEndedInDate",
    ];

    /// <summary>The date of an element of <see cref="Fields"/>.</summary>
    public Stored<DateTime> Get(string field) => field switch
    {
        "devEndedInDate" => DevEndedInDate,
        "onApprovalEndedInDate" => OnApprovalEndedInDate,
        "approvedEndedInDate" => ApprovedEndedInDate,
        "machiningEndedInDate" => MachiningEndedInDate,
        "assemblyEndedInDate" => AssemblyEndedInDate,
        "forInvoicingEndedInDate" => ForInvoicingEndedInDate,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}

/// <summary>Hours of one kind of work.</summary>
/// <param name="NumberOfHoursUsed">Hours registered so far.</param>
/// <param name="BudgetedHours">The quantities of the budget's tasks of this kind.</param>
/// <param name="Deviation">Budgeted − used (sent by the client, checked by the server).</param>
/// <param name="Id">The embedded document's <c>_id</c>.</param>
public sealed record JobHours(double NumberOfHoursUsed, double BudgetedHours, double Deviation, string Id);

/// <summary><c>machiningStatistic</c>: the hours and the three checks (<c>QuestionResponses</c>).</summary>
public sealed record JobMachiningStatistic(
    JobHours Hours,
    string VerificationAndShapeControl,
    string DimensionalVerificationAndControl,
    string AnalysisOfTheDegreeOfFinish,
    string? ValidatedBy);

/// <summary><c>assemblyStatistic</c>: the hours, the six checks and the number of tries of the first test (<c>"1"</c>–<c>"3"</c>).</summary>
public sealed record JobAssemblyStatistic(
    JobHours Hours,
    string PneumaticCircuits,
    string ElectricalBoardsAndCircuits,
    string Kinematics,
    string ProgrammingFeaturesPlc,
    string OperatorEquipmentSafetyConditions,
    string VisualInspection,
    string ValidTryOnFirstTest,
    string? ValidatedBy);

/// <summary>The budgeted hours of a new job (legacy <c>setNewStatistics</c>, PLAN.md §7.3).</summary>
public static class JobStatistics
{
    /// <summary>
    /// Legacy <c>getBudgetedHoursByType</c>: the sum of the quantities of the tasks of <paramref name="taskType"/>,
    /// added in the tasks' order from 0 (JavaScript <c>+=</c>, so <c>0.1 + 0.2</c> is <c>0.30000000000000004</c>).
    /// Consultancy is not counted anywhere. A task without a quantity makes the sum NaN, which the legacy save
    /// refuses (<c>double.NaN</c> here; the caller refuses it).
    /// </summary>
    public static double BudgetedHours(IEnumerable<BudgetTask> tasks, string taskType)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var total = 0.0;
        foreach (var task in tasks)
        {
            if (string.Equals(task.TaskType, taskType, StringComparison.Ordinal))
            {
                total += task.Quantity ?? double.NaN;
            }
        }

        return total;
    }
}
