using Dib4t.Client.Forms;

namespace Dib4t.Client.Auth;

/// <summary>
/// The login form and its client-side check, the legacy yup 0.29 <c>loginSchema</c>
/// (<c>front-end/src/schemas/account/loginSchema.js</c>): email required + yup's e-mail regex (label "Email"),
/// password required + min 5. Messages are yup's defaults. The server's Joi check stays authoritative.
/// </summary>
public sealed class LoginForm
{
    public string? Email { get; set; }

    public string? Password { get; set; }

    public LoginFormErrors Validate()
    {
        var email = string.IsNullOrEmpty(Email) ? "Email is a required field"
            : Yup.IsEmail(Email) ? null : "Email must be a valid email";
        var password = string.IsNullOrEmpty(Password) ? "password is a required field"
            : Password.Length < 5 ? "password must be at least 5 characters" : null;
        return new LoginFormErrors(email, password);
    }
}

/// <summary>Per-field messages shown under the inputs (legacy Formik helper text).</summary>
public sealed record LoginFormErrors(string? Email, string? Password)
{
    public static LoginFormErrors None { get; } = new(null, null);

    public bool Any => Email is not null || Password is not null;
}
