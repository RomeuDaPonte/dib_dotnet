using System.Text.Json;
using System.Text.Json.Serialization;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.SharedKernel;

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
/// A job as the legacy routes answer it (Mongoose <c>toJSON</c>: the defaulted paths first, then <c>_id</c> and the
/// other fields), with its budget as the route populates it: <see cref="BudgetResponse"/> (client and author;
/// never the author's password hash, PLAN.md §6) for create, <c>getById</c>, <c>/:jobId</c> and the saves;
/// <see cref="JobListBudgetResponse"/> (client only) for the lists; <see cref="BudgetRefsResponse"/> for
/// <c>getByBudgetNumber</c>. <see cref="Budget"/> is <c>null</c> when the budget no longer exists. A
/// <see cref="Stored{T}"/> value is left out when missing and written <c>null</c> when stored <c>null</c>.
/// </summary>
public sealed record JobResponse<TBudget>(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> InvoicedPercent,
    double FinalPrice,
    string State,
    string OrderIdentification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<bool> IsOfficialOrderIdentification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> Components,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> Automation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> Materials,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> Others,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> CostNonQuality,
    IReadOnlyList<string?> CommentType,
    [property: JsonPropertyName("_id")] string Id,
    TBudget? Budget,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? StartDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobHoursResponse? DevStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobMachiningStatisticResponse? MachiningStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobAssemblyStatisticResponse? AssemblyStatistic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Priority,
    [property: JsonPropertyName("__v")] int Version)
    where TBudget : class
{
    /// <summary>The comments dialog's text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Stored<string> Comments { get; init; }

    /// <summary>When each state ended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobStatisticsDatesResponse? JobStatisticsDates { get; init; }

    /// <summary>"Data de entrega".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Stored<DateTime> DeliveryDate { get; init; }

    /// <summary>Who billed it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvoicedBy { get; init; }

    /// <summary>Stored fields the legacy schema does not know (e.g. <c>isHightPriority</c> of old jobs).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A job's budget in the lists (legacy <c>populate({ path: "budget", populate: client })</c>): the author is its id.</summary>
public sealed record JobListBudgetResponse(
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    double Margin,
    double DaysNeededToDoTheWork,
    double Validity,
    bool HasGenerateJob,
    [property: JsonPropertyName("_id")] string Id,
    EntityResponse? Client,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Descriptive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonInCharge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MadeBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Total,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date,
    [property: JsonPropertyName("__v")] int Version);

/// <summary>A job's budget of <c>getByBudgetNumber</c> (legacy <c>populate("budget")</c>): client and author are ids.</summary>
public sealed record BudgetRefsResponse(
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    double Margin,
    double DaysNeededToDoTheWork,
    double Validity,
    bool HasGenerateJob,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Descriptive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonInCharge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MadeBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Total,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date,
    [property: JsonPropertyName("__v")] int Version);

/// <summary><c>jobStatisticsDates</c>.</summary>
public sealed record JobStatisticsDatesResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> DevEndedInDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> OnApprovalEndedInDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> ApprovedEndedInDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> MachiningEndedInDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> AssemblyEndedInDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<DateTime> ForInvoicingEndedInDate);

/// <summary><c>PUT /api/job/statistics/:jobId</c> to "Faturada" below 100 % billed: 405 with the saved job (legacy).</summary>
public sealed record JobStatisticsRefusedResponse(string Reason, JobResponse<BudgetResponse> UpdatedJob);

/// <summary><c>GET /api/job/getCounts</c>: the tab counters.</summary>
public sealed record JobCountsResponse(
    int AllCount,
    int DevCount,
    int OnApprovalCount,
    int ApprovedCount,
    int MachiningCount,
    int AssemblyCount,
    int ForInvoicingCount);

/// <summary><c>GET /api/job/getValidTryOnFirstTestCountersByYear/:year</c>: the dashboard's pie chart.</summary>
public sealed record ValidTryCountersResponse(int OneCounter, int TwoCounter, int ThreeCounter);

/// <summary><c>PATCH /api/job/dueDate/:jobId</c> body (the date input's <c>yyyy-MM-dd</c>).</summary>
public sealed record JobDueDateRequest(string DueDate);

/// <summary><c>PATCH /api/job/state/:jobId</c> body.</summary>
public sealed record JobStateRequest(string State);

/// <summary><c>PATCH /api/job/changeJobPriority/:jobId</c> body.</summary>
public sealed record JobPriorityRequest(string Priority);

/// <summary><c>devStatistic</c>.</summary>
public sealed record JobHoursResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> NumberOfHoursUsed,
    double BudgetedHours,
    double Deviation,
    [property: JsonPropertyName("_id")] string Id);

/// <summary><c>machiningStatistic</c>.</summary>
public sealed record JobMachiningStatisticResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> NumberOfHoursUsed,
    double BudgetedHours,
    double Deviation,
    string VerificationAndShapeControl,
    string DimensionalVerificationAndControl,
    string AnalysisOfTheDegreeOfFinish,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValidatedBy);

/// <summary><c>assemblyStatistic</c>.</summary>
public sealed record JobAssemblyStatisticResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Stored<double> NumberOfHoursUsed,
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

/// <summary>
/// <c>POST /api/job/print/:budgetId</c> (the "Imprimir etiquetas" dialog, legacy <c>pintBudgetModel.jsx</c>): which
/// label templates to print, in the legacy key order.
/// </summary>
/// <param name="Maquinacao">The <c>maquinacao</c> template ("Maquinação").</param>
/// <param name="Producao">The <c>producao</c> template ("Produção").</param>
public sealed record PrintLabelsRequest(bool Maquinacao, bool Producao);
