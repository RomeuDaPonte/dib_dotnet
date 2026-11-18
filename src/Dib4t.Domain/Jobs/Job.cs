using Dib4t.Domain.Budgets;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Domain.Jobs;

/// <summary>
/// A job ("obra", legacy <c>models/job.js</c>, collection <c>jobs</c>, PLAN.md §4.2, §7.3): made from one budget
/// revision, it follows the production states of <see cref="JobStates"/>. Values are as stored, with the Mongoose
/// defaults for fields an older document lacks (§4.4).
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="InvoicedPercent">Billed share in percent ("Ft %").</param>
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
    double InvoicedPercent,
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
    int Version);

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
