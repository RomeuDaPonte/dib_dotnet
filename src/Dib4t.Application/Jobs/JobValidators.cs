using System.Text;
using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Jobs;

/// <summary>
/// Legacy <c>validations/jobValidation.js</c> (Joi 14.3.1 + joi-objectid 3.0.1), checked against the legacy code
/// (<c>tests/fixtures/legacy/jobs/job-golden.json</c>): <c>validateNewJob</c>, then what Mongoose 5.13 makes of the
/// <b>original</b> values (the route spreads <c>req.body</c> into <c>new Job({…})</c>, not Joi's converted values).
/// Texts the app stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static class JobValidators
{
    /// <summary>The initial states the create dialog offers (Joi <c>.valid(…)</c>, in its order): not billed, not deleted.</summary>
    public static IReadOnlyList<string> InitialStates { get; } =
        [JobStates.Development, JobStates.OnApproval, JobStates.Approved, JobStates.Machining, JobStates.Assembly, JobStates.ForInvoicing];

    private static readonly JoiObjectSchema NewJobSchema = new(
        ("budgetId", BudgetValidators.ObjectIdRule()),
        ("finalPrice", JoiNumber.Create().Min(1).Required()),
        ("dueDate", JoiDate.Create().Required()),
        ("state", JoiString.Create().Valid(InitialStates).Required()),
        ("orderIdentification", JoiString.Create().Required()),
        ("isOfficialOrderIdentification", JoiBoolean.Create().Required()));

    /// <summary>The states a statistics save may move to (<c>validate*Statistics</c>: every state but "Apagada").</summary>
    public static IReadOnlyList<string> StatisticsStates { get; } =
        [JobStates.Development, JobStates.OnApproval, JobStates.Approved, JobStates.Machining, JobStates.Assembly, JobStates.ForInvoicing, JobStates.Billed];

    private static readonly JoiObjectSchema StatesSchema = new(("state", JoiString.Create().Valid(JobStates.All).Required()));

    private static readonly JoiObjectSchema PrioritySchema = new(("priority", JoiString.Create().Valid(JobPriorities.All).Required()));

    private static readonly JoiObjectSchema DevelopmentStatisticsSchema = new(
        ("commentType", CommentTypeRule()),
        ("numberOfHoursUsed", JoiNumber.Create().Min(0).Required()),
        ("components", JoiNumber.Create().Min(0).Required()),
        ("automation", JoiNumber.Create().Min(0).Required()),
        ("invoicedPercent", JoiNumber.Create().Integer().Min(0).Max(100)),
        ("state", StatisticsStateRule()),
        ("budgetedHours", JoiNumber.Create().Min(0).Required()),
        ("deviation", JoiNumber.Create().Required()),
        ("comments", JoiAny.Create()));

    private static readonly JoiObjectSchema MachiningStatisticsSchema = new(
        ("numberOfHoursUsed", JoiNumber.Create().Integer().Min(0).Required()),
        ("materials", JoiNumber.Create().Integer().Min(0).Required()),
        ("state", StatisticsStateRule()),
        ("budgetedHours", JoiNumber.Create().Min(0).Required()),
        ("invoicedPercent", JoiNumber.Create().Integer().Min(0).Max(100).Required()),
        ("deviation", JoiNumber.Create().Required()),
        ("verificationAndShapeControl", QuestionRule()),
        ("dimensionalVerificationAndControl", QuestionRule()),
        ("analysisOfTheDegreeOfFinish", QuestionRule()),
        ("comments", JoiAny.Create()),
        ("commentType", CommentTypeRule()));

    private static readonly JoiObjectSchema AssemblyStatisticsSchema = new(
        ("numberOfHoursUsed", JoiNumber.Create().Integer().Min(0).Required()),
        ("components", JoiNumber.Create().Integer().Min(0).Required()),
        ("commentType", CommentTypeRule()),
        ("automation", JoiNumber.Create().Integer().Min(0).Required()),
        ("state", StatisticsStateRule()),
        ("budgetedHours", JoiNumber.Create().Integer().Min(0).Required()),
        ("invoicedPercent", JoiNumber.Create().Integer().Min(0).Max(100).Required()),
        ("deviation", JoiNumber.Create().Integer().Required()),
        ("pneumaticCircuits", QuestionRule()),
        ("electricalBoardsAndCircuits", QuestionRule()),
        ("kinematics", QuestionRule()),
        ("programmingFeaturesPLC", QuestionRule()),
        ("operator_equipmentSafetyConditions", QuestionRule()),
        ("visualInspection", QuestionRule()),
        ("validTryOnFirstTest", JoiString.Create().Valid(QuestionResponses.FirstTestTries).Required()),
        ("deliveryDate", JoiAny.Create()),
        ("comments", JoiAny.Create()));

    private static readonly JoiObjectSchema ForInvoicingStatisticsSchema = new(
        ("deliveryDate", JoiAny.Create()),
        ("orderIdentification", JoiString.Create().Required()),
        ("isOfficialOrderIdentification", JoiBoolean.Create().Required()),
        ("invoicedPercent", JoiNumber.Create().Integer().Min(0).Max(100).Required()),
        ("state", StatisticsStateRule()),
        ("comments", JoiAny.Create()),
        ("commentType", CommentTypeRule()));

    /// <summary>
    /// <c>validateJobId</c> / <c>validateBudetId</c>: the route parameter <paramref name="name"/> must be 24 hex digits
    /// (joi-objectid), else <c>"jobId" with value "abc" fails to match the valid mongo id pattern</c>. The id in
    /// lower case.
    /// </summary>
    public static Result<string> ValidateId(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var schema = new JoiObjectSchema((name, BudgetValidators.ObjectIdRule()));
        var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { [name] = value });
        return schema.Validate(parameters) is { } message ? Error.Validation(message) : value!.ToLowerInvariant();
    }

    /// <summary><c>validateStates</c> of a body (<c>PATCH state</c>) or of the route (<c>getByState/:state</c>): the state.</summary>
    public static Result<string> ValidateState(JsonElement body) =>
        StatesSchema.Validate(body) is { } message ? Error.Validation(message) : body.GetProperty("state").GetString()!;

    /// <summary><c>validateJobPriority</c>: the priority.</summary>
    public static Result<string> ValidatePriority(JsonElement body) =>
        PrioritySchema.Validate(body) is { } message ? Error.Validation(message) : body.GetProperty("priority").GetString()!;

    /// <summary>
    /// <c>validateStatistics(req, currentJob.state)</c>: the schema of the job's <b>current</b> state (development,
    /// approval and approved share one); "Faturada", "Apagada" (and any other state) have none: every body passes.
    /// The 400 text, or <c>null</c>.
    /// </summary>
    public static string? ValidateStatistics(JsonElement body, string? currentState)
    {
        var schema = currentState switch
        {
            JobStates.Development or JobStates.OnApproval or JobStates.Approved => DevelopmentStatisticsSchema,
            JobStates.Machining => MachiningStatisticsSchema,
            JobStates.Assembly => AssemblyStatisticsSchema,
            JobStates.ForInvoicing => ForInvoicingStatisticsSchema,
            _ => null,
        };
        return schema?.Validate(body);
    }

    private static JoiArray CommentTypeRule() => JoiArray.Create().Items(JoiString.Create().Valid(CommentTypes.All));

    private static JoiString StatisticsStateRule() => JoiString.Create().Valid(StatisticsStates).Required();

    private static JoiString QuestionRule() => JoiString.Create().Valid(QuestionResponses.All).Required();

    /// <summary>
    /// <c>POST /api/job</c>: the 400 text, else the values as Mongoose would store them. A value Joi accepts but the
    /// legacy save refuses (<c>"TRUE"</c> as the flag, a date only Joi reads) has <see cref="NewJobInput.Refusal"/>:
    /// legacy saves the budget's <c>hasGenerateJob</c> and then fails; the new app refuses before writing (D26).
    /// </summary>
    public static Result<NewJobInput> ValidateNew(JsonElement body)
    {
        if (NewJobSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        var finalPrice = JoiNumber.Convert(properties["finalPrice"]).Value; // Number(text) gives the same value
        string? refusal = null;
        DateTime? dueDate = null;
        if (MongooseCasts.Date(properties["dueDate"]) is not { } time)
        {
            refusal = "dueDate: Cast to date failed (Joi accepts it, Mongoose does not)";
        }
        else if (JsDate.ToDateTime(time) is not { } date)
        {
            refusal = $"dueDate: {JsDate.ToIsoString(time)} is outside the years 1–9999";
        }
        else
        {
            dueDate = date;
        }

        var isOfficial = MongooseCasts.Boolean(properties["isOfficialOrderIdentification"]);
        if (isOfficial is null)
        {
            refusal ??= $"isOfficialOrderIdentification: Cast to Boolean failed for value {properties["isOfficialOrderIdentification"].GetRawText()}";
        }

        return new NewJobInput(
            properties["budgetId"].GetString()!.ToLowerInvariant(),
            finalPrice,
            dueDate ?? DateTime.UnixEpoch,
            properties["state"].GetString()!,
            properties["orderIdentification"].GetString()!.Normalize(NormalizationForm.FormC),
            isOfficial ?? false)
        {
            Refusal = refusal,
        };
    }
}

/// <summary>A create body that passed Joi: the values as the legacy model stores them (id lower case, text NFC).</summary>
public sealed record NewJobInput(
    string BudgetId,
    double FinalPrice,
    DateTime DueDate,
    string State,
    string OrderIdentification,
    bool IsOfficialOrderIdentification)
{
    /// <summary>Why the legacy save would refuse the job (D26), or <c>null</c>.</summary>
    public string? Refusal { get; init; }
}
