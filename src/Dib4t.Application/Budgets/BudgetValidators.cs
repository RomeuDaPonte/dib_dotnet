using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;

namespace Dib4t.Application.Budgets;

/// <summary>
/// Legacy <c>validations/budgetValidation.js</c> (Joi 14.3.1 + joi-objectid 3.0.1), checked against the legacy code
/// (<c>tests/fixtures/legacy/budgets/budget-golden.json</c>): <c>validateNewBudget</c> (create and clone),
/// the schema <c>validateUpdateBudget</c> was meant to apply (D7: legacy skips it; the new app applies it), and
/// <c>validateSearch</c>. Then D21 (ADR-0005): a new descriptive must not contain <c>/</c>. Strings the app
/// stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static partial class BudgetValidators
{
    private static readonly JoiObjectSchema NewBudgetSchema = new(
        ("clientId", ObjectIdRule()),
        ("madeById", ObjectIdRule()),
        ("descriptive", JoiString.Create().Required()),
        ("personInCharge", JoiString.Create().Required()));

    private static readonly JoiObjectSchema UpdateSchema = new(
        ("madeById", ObjectIdRule()),
        ("descriptive", JoiString.Create().Required()),
        ("personInCharge", JoiString.Create().Required()));

    private static readonly JoiObjectSchema SearchSchema = new(("q", JoiString.Create().Min(2).Required()));

    /// <summary><c>POST /api/budget</c> and <c>POST /api/budget/clone/:id</c>: the 400 text, else the values.</summary>
    public static Result<NewBudgetInput> ValidateNew(JsonElement body)
    {
        if (NewBudgetSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        var input = new NewBudgetInput(
            Id(properties["clientId"]), Id(properties["madeById"]), Text(properties["descriptive"]), Text(properties["personInCharge"]));
        return HasSlash(input.Descriptive) ? Error.Validation(BudgetMessages.DescriptiveWithSlash) : input;
    }

    /// <summary>
    /// <c>PUT /api/budget/:id</c> (D7: the intended schema; the legacy edit dialog always sends the three fields).
    /// The D21 check needs the stored descriptive (only a <b>changed</b> descriptive is refused): see
    /// <see cref="BudgetsService.UpdateAsync"/>.
    /// </summary>
    public static Result<BudgetEditInput> ValidateUpdate(JsonElement body)
    {
        if (UpdateSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        return new BudgetEditInput(Id(properties["madeById"]), Text(properties["descriptive"]), Text(properties["personInCharge"]));
    }

    /// <summary>
    /// <c>GET /api/budget/search</c>: <paramref name="query"/> is the query string as the legacy Express parser
    /// (qs, "extended") builds it: one key per name, a repeated name or a <c>name[…]</c> key is not a string. The 400
    /// text, else the search text (the user's regular expression).
    /// </summary>
    public static Result<string> ValidateSearch(JsonElement query) =>
        SearchSchema.Validate(query) is { } message ? Error.Validation(message) : JsObject.Properties(query)["q"].GetString()!;

    /// <summary>D21: a descriptive with <c>/</c> would create nested NAS folders.</summary>
    public static bool HasSlash(string descriptive) => descriptive.Contains('/', StringComparison.Ordinal);

    /// <summary>joi-objectid 3.0.1: <c>Joi.string().regex(/^[0-9a-fA-F]{24}$/, "valid mongo id").required()</c>.</summary>
    internal static JoiString ObjectIdRule() => JoiString.Create().Regex(MongoId(), "valid mongo id").Required();

    private static string Id(JsonElement value) => value.GetString()!.ToLowerInvariant();

    private static string Text(JsonElement value) => value.GetString()!.Normalize(NormalizationForm.FormC);

    // JavaScript's $ (no m flag) is the end of the input: \z.
    [GeneratedRegex("^[0-9a-fA-F]{24}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex MongoId();
}

/// <summary>A create or clone body that passed the legacy validation (ids lower case, texts NFC).</summary>
public sealed record NewBudgetInput(string ClientId, string MadeById, string Descriptive, string PersonInCharge);

/// <summary>An edit body that passed the validation (id lower case, texts NFC).</summary>
public sealed record BudgetEditInput(string MadeById, string Descriptive, string PersonInCharge);
