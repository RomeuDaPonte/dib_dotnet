using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;

namespace Dib4t.Application.Account;

/// <summary>
/// Legacy <c>validateLogin</c> (<c>validations/userValidation.js</c>):
/// <c>{ email: string().min(5).max(255).required().email(), password: string().min(5).max(255).required() }</c>.
/// </summary>
public static class LoginValidator
{
    private static readonly JoiObjectSchema Schema = new(
        ("email", JoiString.Create().Min(5).Max(255).Required().Email()),
        ("password", JoiString.Create().Min(5).Max(255).Required()));

    /// <summary>The credentials, or the legacy 400 text of the first problem.</summary>
    public static Result<LoginCredentials> Validate(JsonElement body)
    {
        if (Schema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body); // the value JSON.parse keeps for a duplicate key (the last one)
        return new LoginCredentials(properties["email"].GetString()!, properties["password"].GetString()!);
    }
}
