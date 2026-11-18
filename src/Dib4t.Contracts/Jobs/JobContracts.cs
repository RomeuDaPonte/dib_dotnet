using System.Text.Json.Serialization;
using Dib4t.Contracts.Budgets;

namespace Dib4t.Contracts.Jobs;

/// <summary>
/// The body of <c>POST /api/job</c> as the legacy "Criar obra" dialog sends it (<c>newJobModal.jsx</c>). The server
/// validates the raw JSON (Joi port); this type is what the client sends. <see cref="FinalPrice"/> is the budget's
/// total (a number) until the user types, then the field's text (as legacy, Formik keeps the input's string).
/// </summary>
public sealed record NewJobRequest(
    string BudgetId,
    object FinalPrice,
    string DueDate,
    string State,
    string OrderIdentification,
    bool IsOfficialOrderIdentification);

/// <summary>
/// A job as legacy answers <c>POST /api/job</c> (Mongoose <c>toJSON</c>: the defaulted paths first, then <c>_id</c>
/// and the other fields in the order they were set), with the populated budget it was made from (its client and
/// author populated too; never the author's password hash, PLAN.md §6).
/// </summary>
public sealed record JobResponse(
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
    [property: JsonPropertyName("_id")] string Id,
    BudgetResponse? Budget,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? StartDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobHoursResponse? DevStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobMachiningStatisticResponse? MachiningStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobAssemblyStatisticResponse? AssemblyStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Priority,
    [property: JsonPropertyName("__v")] int Version);

/// <summary><c>devStatistic</c>.</summary>
public sealed record JobHoursResponse(
    double NumberOfHoursUsed,
    double BudgetedHours,
    double Deviation,
    [property: JsonPropertyName("_id")] string Id);

/// <summary><c>machiningStatistic</c>.</summary>
public sealed record JobMachiningStatisticResponse(
    double NumberOfHoursUsed,
    double BudgetedHours,
    double Deviation,
    string VerificationAndShapeControl,
    string DimensionalVerificationAndControl,
    string AnalysisOfTheDegreeOfFinish,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValidatedBy);

/// <summary><c>assemblyStatistic</c>.</summary>
public sealed record JobAssemblyStatisticResponse(
    double NumberOfHoursUsed,
    double BudgetedHours,
    double Deviation,
    string PneumaticCircuits,
    string ElectricalBoardsAndCircuits,
    string Kinematics,
    [property: JsonPropertyName("programmingFeaturesPLC")] string ProgrammingFeaturesPlc,
    [property: JsonPropertyName("operator_equipmentSafetyConditions")] string OperatorEquipmentSafetyConditions,
    string VisualInspection,
    string ValidTryOnFirstTest,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValidatedBy);
