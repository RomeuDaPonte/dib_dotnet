using System.Text;
using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Account;

/// <summary>
/// Legacy <c>validateUser</c> / <c>validateEditarUser</c> (<c>validations/userValidation.js</c>, Joi 14.3.1) and what
/// the user model then does with the body (Mongoose casts, the schema's <c>minlength</c>, <c>enum</c> and
/// <c>required</c>), checked against the legacy code (<c>settings-golden.json</c>). A body Joi accepts but the model
/// refuses fails the legacy save with Express's error page: here a <see cref="ErrorKind.Failure"/> with
/// <see cref="LegacyMessages.SomethingFailed"/> (D16). Strings the app stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static class UserValidators
{
    private static readonly JoiObjectSchema CreateSchema = new(
        ("name", JoiString.Create().Min(3).Required()),
        ("email", JoiString.Create().Min(10).Required().Email()),
        ("password", JoiString.Create().Min(5).Max(255).Required()),
        ("passwordConfirmation", new JoiRef("password")),
        ("roles", JoiArray.Create().Min(1)),
        ("fieldOfCompetencies", JoiString.Create()));

    private static readonly JoiObjectSchema EditSchema = new(
        ("name", JoiString.Create().Required()),
        ("email", JoiString.Create().Required().Email()),
        ("roles", JoiArray.Create().Min(1)),
        ("password", JoiAny.Create()),
        ("fieldOfCompetencies", JoiString.Create()));

    /// <summary><c>POST /api/account/create</c>: the 400 Joi text, else the body (cast with <see cref="UserBody.ToInput"/>).</summary>
    public static Result<UserBody> ValidateCreate(JsonElement body) =>
        CreateSchema.Validate(body) is { } message ? Error.Validation(message) : new UserBody(JsObject.Properties(body), IsCreate: true);

    /// <summary><c>PUT /api/account/:id</c>: the 400 Joi text, else the body (cast with <see cref="UserBody.ToInput"/>).</summary>
    public static Result<UserBody> ValidateEdit(JsonElement body) =>
        EditSchema.Validate(body) is { } message ? Error.Validation(message) : new UserBody(JsObject.Properties(body), IsCreate: false);

    /// <summary>
    /// What the model makes of a valid body. Create: missing roles → <c>[]</c>, missing field of competencies →
    /// the default. Edit (<c>user.update(body)</c>): a missing <c>fieldOfCompetencies</c> is assigned
    /// <c>undefined</c> and fails the schema's <c>required</c>; a missing <c>roles</c> removes the stored array; a
    /// name under 3 or an e-mail under 10 characters fails the schema's <c>minlength</c> (Joi lets them through).
    /// </summary>
    internal static Result<UserInput> ToInput(UserBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var properties = body.Properties;
        return body.IsCreate
            ? ToInput(properties, properties["password"], rolesDefault: [], fieldDefault: FieldsOfCompetence.Default)
            : ToInput(properties, properties.TryGetValue("password", out var password) ? password : null, rolesDefault: null, fieldDefault: null);
    }

    private static Result<UserInput> ToInput(
        IReadOnlyDictionary<string, JsonElement> properties, JsonElement? password, List<string?>? rolesDefault, string? fieldDefault)
    {
        // The schema's minlength counts the text as sent (JavaScript length), before the NFC normalization.
        var name = properties["name"].GetString()!;
        var email = properties["email"].GetString()!;
        if (name.Length < 3)
        {
            return Refused($"name `{name}` is shorter than the minimum allowed length (3)");
        }

        if (email.Length < 10)
        {
            return Refused($"email `{email}` is shorter than the minimum allowed length (10)");
        }

        var roles = rolesDefault;
        if (properties.TryGetValue("roles", out var rolesValue))
        {
            var (cast, failure) = MongooseCast.StringArray(rolesValue);
            if (failure is not null)
            {
                return Refused("roles: " + failure);
            }

            roles = [.. cast!.Select(role => role is null ? null : Nfc(role))];
        }

        var field = properties.TryGetValue("fieldOfCompetencies", out var fieldValue) ? Nfc(fieldValue.GetString()!) : fieldDefault;
        if (field is null)
        {
            return Refused("fieldOfCompetencies is required");
        }

        if (!FieldsOfCompetence.All.Contains(field, StringComparer.Ordinal))
        {
            return Refused($"fieldOfCompetencies `{field}` is not a valid enum value");
        }

        var passwordValue = password is { } value ? PasswordInput.Of(value) : PasswordInput.None;
        return new UserInput(Nfc(name), Nfc(email), roles, field, passwordValue);
    }

    private static Error Refused(string reason) => new(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };

    private static string Nfc(string value) => value.Normalize(NormalizationForm.FormC);
}

/// <summary>A create or edit body that passed Joi.</summary>
public sealed record UserBody(IReadOnlyDictionary<string, JsonElement> Properties, bool IsCreate)
{
    /// <summary>The e-mail as sent (a string: Joi checked it).</summary>
    public string Email => Properties["email"].GetString()!;

    /// <summary>The values to store, or the model's refusal (500).</summary>
    public Result<UserInput> ToInput() => UserValidators.ToInput(this);

    /// <summary>Never print the body (it may hold a password).</summary>
    public override string ToString() => $"UserBody {{ Email = {Email} }}";
}

/// <summary>
/// The user values a create or edit body gives, after the legacy validation and casts. <see cref="Roles"/> is
/// <c>null</c> on edit when the body has no roles (the stored array is removed).
/// </summary>
public sealed record UserInput(string Name, string Email, IReadOnlyList<string?>? Roles, string FieldOfCompetencies, PasswordInput Password)
{
    /// <summary>Never print the password.</summary>
    public override string ToString() => $"UserInput {{ Name = {Name}, Email = {Email}, FieldOfCompetencies = {FieldOfCompetencies} }}";
}

/// <summary>
/// The password of a body, as the legacy route treats it: <b>none</b> (missing or falsy: <c>""</c>, <c>null</c>,
/// <c>0</c>, <c>false</c>, so it is not changed), <b>a text</b> to hash, or a truthy <b>non-string</b> that bcryptjs
/// refuses ("Illegal arguments", 500). Passwords are never trimmed, normalized or logged.
/// </summary>
public sealed class PasswordInput
{
    private PasswordInput(string? text, string? illegalType)
    {
        Text = text;
        IllegalType = illegalType;
    }

    public static PasswordInput None { get; } = new(null, null);

    /// <summary>The password to hash; <c>null</c> when there is none or it is not a string.</summary>
    public string? Text { get; }

    /// <summary>The JavaScript type of a truthy non-string password (bcryptjs: <c>Illegal arguments: number, string</c>).</summary>
    public string? IllegalType { get; }

    public static PasswordInput Of(JsonElement value)
    {
        if (!MongooseCast.IsTruthy(value))
        {
            return None;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => new PasswordInput(value.GetString(), null),
            JsonValueKind.Number => new PasswordInput(null, "number"),
            JsonValueKind.True => new PasswordInput(null, "boolean"),
            _ => new PasswordInput(null, "object"),
        };
    }

    public override string ToString() => Text is null ? $"PasswordInput {{ IllegalType = {IllegalType} }}" : "PasswordInput { *** }";
}
