using System.Text.RegularExpressions;

namespace Dib4t.Client.Auth;

/// <summary>
/// The login form and its client-side check, the legacy yup 0.29 <c>loginSchema</c>
/// (<c>front-end/src/schemas/account/loginSchema.js</c>): email required + yup's e-mail regex (label "Email"),
/// password required + min 5. Messages are yup's defaults. The server's Joi check stays authoritative.
/// </summary>
public sealed partial class LoginForm
{
    public string? Email { get; set; }

    public string? Password { get; set; }

    public LoginFormErrors Validate()
    {
        var email = string.IsNullOrEmpty(Email) ? "Email is a required field"
            : YupEmail().IsMatch(Email) ? null : "Email must be a valid email";
        var password = string.IsNullOrEmpty(Password) ? "password is a required field"
            : Password.Length < 5 ? "password must be at least 5 characters" : null;
        return new LoginFormErrors(email, password);
    }

    /// <summary>yup 0.29.3 <c>rEmail</c> (case-insensitive), with JavaScript's ASCII <c>\d</c> as <c>[0-9]</c> and <c>$</c> as <c>\z</c>.</summary>
    [GeneratedRegex("""^((([a-z]|[0-9]|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+(\.([a-z]|[0-9]|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+)*)|((\x22)((((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(([\x01-\x08\x0b\x0c\x0e-\x1f\x7f]|\x21|[\x23-\x5b]|[\x5d-\x7e]|[ -퟿豈-﷏ﷰ-￯])|(\\([\x01-\x09\x0b\x0c\x0d-\x7f]|[ -퟿豈-﷏ﷰ-￯]))))*(((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(\x22)))@((([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])|(([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])([a-z]|[0-9]|-|\.|_|~|[ -퟿豈-﷏ﷰ-￯])*([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])))\.)+(([a-z]|[ -퟿豈-﷏ﷰ-￯])|(([a-z]|[ -퟿豈-﷏ﷰ-￯])([a-z]|[0-9]|-|\.|_|~|[ -퟿豈-﷏ﷰ-￯])*([a-z]|[ -퟿豈-﷏ﷰ-￯])))\z""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YupEmail();
}

/// <summary>Per-field messages shown under the inputs (legacy Formik helper text).</summary>
public sealed record LoginFormErrors(string? Email, string? Password)
{
    public static LoginFormErrors None { get; } = new(null, null);

    public bool Any => Email is not null || Password is not null;
}
