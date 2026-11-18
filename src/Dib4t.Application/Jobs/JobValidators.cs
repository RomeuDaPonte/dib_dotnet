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
