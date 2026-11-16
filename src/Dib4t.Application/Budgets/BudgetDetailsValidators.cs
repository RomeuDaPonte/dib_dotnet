using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Entities;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Budgets;

/// <summary>
/// The budget details validations (legacy <c>validations/budgetValidation.js</c> <c>validatePatch</c> and
/// <c>validateImportBudget</c>, <c>validations/taskValidation.js</c>; Joi 14.3.1 + joi-objectid), checked against
/// the legacy routes themselves (<c>tests/fixtures/legacy/budget-details/budget-details-golden.json</c>), plus what
/// the Mongoose models then store: the <b>original</b> value goes to the model, so a number text is stored as its
/// number, and a boolean text Joi accepts but Mongoose cannot cast (<c>"TRUE"</c>) is a refused save (500).
/// Strings the app stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static partial class BudgetDetailsValidators
{
    /// <summary>The properties <c>patchProperty</c> may change (legacy <c>helpers/patchBudgetPropertyNames.js</c>).</summary>
    public static IReadOnlyList<string> PatchableProperties { get; } =
        [BudgetFields.DaysNeededToDoTheWork, BudgetFields.Total, BudgetFields.PaymentMethod, BudgetFields.Margin, BudgetFields.Description, BudgetFields.Complete, BudgetFields.Validity];

    private static readonly JoiObjectSchema PropertyNameSchema = new(
        ("propertyName", JoiString.Create().Valid(PatchableProperties).Required()),
        ("value", JoiAny.Create()));

    private static readonly Dictionary<string, JoiObjectSchema> ValueSchemas = new(StringComparer.Ordinal)
    {
        [BudgetFields.Total] = PatchSchema(JoiNumber.Create().Min(0).Required()),
        [BudgetFields.DaysNeededToDoTheWork] = PatchSchema(JoiNumber.Create().Min(0).Required()),
        [BudgetFields.PaymentMethod] = PatchSchema(JoiString.Create().Required()),
        [BudgetFields.Margin] = PatchSchema(JoiNumber.Create().Min(1).Required()),
        [BudgetFields.Description] = PatchSchema(JoiString.Create().Required()),
        [BudgetFields.Complete] = PatchSchema(JoiBoolean.Create().Required()),
        [BudgetFields.Validity] = PatchSchema(JoiNumber.Create().Integer().Min(1).Required()),
    };

    private static readonly JoiObjectSchema ImportSchema = new(
        ("importDescription", JoiBoolean.Create().Required()),
        ("importTasks", JoiBoolean.Create().Required()),
        ("budgetNumber", JoiString.Create().Required()));

    private static readonly JoiObjectSchema TaskSchema = new(TaskRules());

    /// <summary>D8: the schema <c>POST /api/task</c> was meant to apply (legacy refuses every body: "budgetId" is not allowed).</summary>
    private static readonly JoiObjectSchema NewTaskSchema = new([.. TaskRules(), ("budgetId", ObjectIdRule())]);

    private static readonly JoiObjectSchema TaskIdsSchema = new(("taskId", ObjectIdRule()), ("budgetId", ObjectIdRule()));

    /// <summary>
    /// <c>PUT /api/budget/patchProperty/:id</c>: the property and its value, or the 400
    /// <c>"Não é possível atualizar " + String(propertyName)</c> (<c>undefined</c> when missing, arrays joined). A value
    /// that passes Joi but that Mongoose cannot cast (<c>"TRUE"</c>) passes with <see cref="BudgetPatch.CastFailure"/>: the
    /// save fails later (500), after the route's lookups.
    /// </summary>
    public static Result<BudgetPatch> ValidatePatch(JsonElement body)
    {
        var properties = body.ValueKind == JsonValueKind.Object ? JsObject.Properties(body) : new Dictionary<string, JsonElement>();
        if (PropertyNameSchema.Validate(body) is not null
            || ValueSchemas[properties["propertyName"].GetString()!].Validate(body) is not null)
        {
            var name = properties.TryGetValue("propertyName", out var given) ? EntitySearch.JsString(given) : "undefined";
            return name is null
                ? new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = "String(propertyName) throws (own toString key)" }
                : Error.Validation(BudgetMessages.PatchRefusedPrefix + name);
        }

        var field = properties["propertyName"].GetString()!;
        var value = properties["value"];
        switch (field)
        {
            case BudgetFields.PaymentMethod or BudgetFields.Description:
                return BudgetPatch.OfText(field, Text(value));
            case BudgetFields.Complete:
                // Mongoose castBoolean: only true/false and "true"/"false" of what Joi lets through.
                return value.ValueKind switch
                {
                    JsonValueKind.True => BudgetPatch.OfFlag(field, true),
                    JsonValueKind.False => BudgetPatch.OfFlag(field, false),
                    _ when value.GetString() is "true" => BudgetPatch.OfFlag(field, true),
                    _ when value.GetString() is "false" => BudgetPatch.OfFlag(field, false),
                    _ => new BudgetPatch(field, null, null, null)
                    {
                        CastFailure = $"Budget validation failed: complete: Cast to Boolean failed for value \"{value.GetString()}\" (type string) at path \"complete\"",
                    },
                };
            default:
                return BudgetPatch.OfNumber(field, JoiNumber.Convert(value).Value);
        }
    }

    /// <summary>
    /// <c>PUT /api/budget/import/:id</c>: the 400 <c>"Erro: " + Joi's text</c>, else what to import. The route then tests
    /// the <b>original</b> values' JavaScript truthiness, so the text <c>"false"</c> (which Joi accepts) imports too.
    /// </summary>
    public static Result<BudgetImportInput> ValidateImport(JsonElement body)
    {
        if (ImportSchema.Validate(body) is { } message)
        {
            return Error.Validation(BudgetMessages.ImportRefusedPrefix + message);
        }

        var properties = JsObject.Properties(body);
        return new BudgetImportInput(
            MongooseCast.IsTruthy(properties["importDescription"]),
            MongooseCast.IsTruthy(properties["importTasks"]),
            properties["budgetNumber"].GetString()!);
    }

    /// <summary><c>PUT /api/task/:taskId/:budgetId</c> body (<c>validateTask</c>): the 400 text, else the values.</summary>
    public static Result<TaskInput> ValidateTask(JsonElement body) =>
        TaskSchema.Validate(body) is { } message ? Error.Validation(message) : ToTaskInput(JsObject.Properties(body));

    /// <summary>D8: <c>POST /api/task</c> with the intended schema (the task's fields and its <c>budgetId</c>).</summary>
    public static Result<(TaskInput Task, string BudgetId)> ValidateNewTask(JsonElement body)
    {
        if (NewTaskSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        return (ToTaskInput(properties), properties["budgetId"].GetString()!.ToLowerInvariant());
    }

    /// <summary>The route ids of the task edit and delete (<c>validateId(req.params)</c>), lower case.</summary>
    public static Result<(string TaskId, string BudgetId)> ValidateTaskIds(string taskId, string budgetId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(budgetId);
        var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["taskId"] = taskId, ["budgetId"] = budgetId });
        return TaskIdsSchema.Validate(parameters) is { } message
            ? Error.Validation(message)
            : (taskId.ToLowerInvariant(), budgetId.ToLowerInvariant());
    }

    private static (string Key, JoiRule Rule)[] TaskRules() =>
    [
        ("taskType", JoiString.Create().Valid(TaskTypes.All).Required()),
        ("description", JoiString.Create().AllowEmpty()),
        ("unitPrice", JoiNumber.Create().Min(0).Required()),
        ("quantity", JoiNumber.Create().Min(0).Required()),
    ];

    private static TaskInput ToTaskInput(IReadOnlyDictionary<string, JsonElement> properties) => new(
        properties["taskType"].GetString()!,
        properties.TryGetValue("description", out var description) ? Text(description) : null,
        JoiNumber.Convert(properties["unitPrice"]).Value,
        JoiNumber.Convert(properties["quantity"]).Value);

    private static JoiObjectSchema PatchSchema(JoiRule value) => new(
        ("propertyName", JoiString.Create().Valid(PatchableProperties).Required()),
        ("value", value));

    /// <summary>joi-objectid 3.0.1: <c>Joi.string().regex(/^[0-9a-fA-F]{24}$/, "valid mongo id").required()</c>.</summary>
    private static JoiString ObjectIdRule() => JoiString.Create().Regex(MongoId(), "valid mongo id").Required();

    private static string Text(JsonElement value) => value.GetString()!.Normalize(NormalizationForm.FormC);

    [GeneratedRegex("^[0-9a-fA-F]{24}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex MongoId();
}

/// <summary>The budget fields the details page changes (stored names).</summary>
public static class BudgetFields
{
    public const string DaysNeededToDoTheWork = "daysNeededToDoTheWork";
    public const string Total = "total";
    public const string PaymentMethod = "paymentMethod";
    public const string Margin = "margin";
    public const string Description = "description";
    public const string Complete = "complete";
    public const string Validity = "validity";
}

/// <summary>A <c>patchProperty</c> that passed the validation: the field and its value as Mongoose casts it.</summary>
/// <param name="Field">One of <see cref="BudgetDetailsValidators.PatchableProperties"/>.</param>
/// <param name="Number">The value of a number field.</param>
/// <param name="Text">The value of a text field (NFC).</param>
/// <param name="Flag">The value of <c>complete</c>.</param>
public sealed record BudgetPatch(string Field, double? Number, string? Text, bool? Flag)
{
    /// <summary>Why Mongoose cannot cast the value (its save then fails, 500); <c>null</c> for a value it stores.</summary>
    public string? CastFailure { get; init; }

    public static BudgetPatch OfNumber(string field, double value) => new(field, value, null, null);

    public static BudgetPatch OfText(string field, string value) => new(field, null, value, null);

    public static BudgetPatch OfFlag(string field, bool value) => new(field, null, null, value);
}

/// <summary>An import body that passed the validation (the flags as the route tests them: JavaScript truthiness).</summary>
public sealed record BudgetImportInput(bool ImportDescription, bool ImportTasks, string BudgetNumber);

/// <summary>
/// A task body that passed <c>validateTask</c>: the values Mongoose stores (<c>task.update</c>); a missing
/// description is <c>null</c> (<c>undefined</c>: removed from a stored task).
/// </summary>
public sealed record TaskInput(string TaskType, string? Description, double UnitPrice, double Quantity);
