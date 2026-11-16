using System.Text;
using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Entities;

/// <summary>
/// Legacy <c>validations/entityValidation.js</c> (Joi 14.3.1, the same schema for create and edit) and what the
/// entity model then does with the body (Mongoose 5.13 String casts of the four <c>Joi.any()</c> fields), checked
/// against the legacy code (<c>entity-golden.json</c>). A value Joi accepts but Mongoose cannot cast (an object, an
/// array) fails the legacy save with Express's error page: here a <see cref="ErrorKind.Failure"/> with
/// <see cref="LegacyMessages.SomethingFailed"/> (D16). Strings the app stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static class EntityValidators
{
    private static readonly JoiObjectSchema Schema = new(
        ("name", JoiString.Create().Required()),
        ("fiscalNumber", JoiAny.Create()),
        ("address", JoiAny.Create()),
        ("zipCode", JoiAny.Create()),
        ("location", JoiAny.Create()),
        ("paymentCondition", JoiString.Create().Required()),
        ("type", JoiString.Create().Valid(EntityTypes.All).Required()));

    /// <summary><c>POST /api/entity</c> and <c>PUT /api/entity/:id</c>: the 400 Joi text, else the body (cast with <see cref="EntityBody.ToInput"/>).</summary>
    public static Result<EntityBody> Validate(JsonElement body) =>
        Schema.Validate(body) is { } message ? Error.Validation(message) : new EntityBody(JsObject.Properties(body));

    internal static Result<EntityInput> ToInput(EntityBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var properties = body.Properties;
        var texts = new EntityText[4];
        string[] optional = ["fiscalNumber", "address", "zipCode", "location"];
        for (var i = 0; i < optional.Length; i++)
        {
            if (!properties.TryGetValue(optional[i], out var value))
            {
                texts[i] = EntityText.Missing;
                continue;
            }

            var (cast, failure) = MongooseCast.String(value);
            if (failure is not null)
            {
                return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = $"{optional[i]}: {failure}" };
            }

            texts[i] = cast is null ? EntityText.Null : EntityText.Of(Nfc(cast));
        }

        return new EntityInput(
            body.Name, texts[0], texts[1], texts[2], texts[3], Nfc(properties["paymentCondition"].GetString()!), properties["type"].GetString()!);
    }

    private static string Nfc(string value) => value.Normalize(NormalizationForm.FormC);
}

/// <summary>A create or edit body that passed Joi.</summary>
public sealed record EntityBody(IReadOnlyDictionary<string, JsonElement> Properties)
{
    /// <summary>The name as it is stored (NFC): the duplicate check on create uses this form.</summary>
    public string Name => Properties["name"].GetString()!.Normalize(NormalizationForm.FormC);

    /// <summary>The values to store, or the model's refusal (500).</summary>
    public Result<EntityInput> ToInput() => EntityValidators.ToInput(this);
}

/// <summary>
/// The values of an entity body after the legacy validation and casts, in the legacy UI's field order
/// (<c>entityService.js</c>). The optional texts keep JavaScript's three states: missing (the key was not sent),
/// <c>null</c>, or a string.
/// </summary>
public sealed record EntityInput(
    string Name,
    EntityText FiscalNumber,
    EntityText Address,
    EntityText ZipCode,
    EntityText Location,
    string PaymentCondition,
    string Type);

/// <summary>
/// An optional text of an entity body: <see cref="Missing"/> (<c>undefined</c>: not stored on create; on edit
/// <c>entity.set({ x: undefined })</c> removes the stored field), <see cref="Null"/> (stored as BSON null) or a
/// string.
/// </summary>
public readonly record struct EntityText(bool IsPresent, string? Value)
{
    public static EntityText Missing => default;

    public static EntityText Null => new(true, null);

    public static EntityText Of(string value) => new(true, value ?? throw new ArgumentNullException(nameof(value)));
}
