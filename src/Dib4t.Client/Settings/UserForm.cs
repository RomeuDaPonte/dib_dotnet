using Dib4t.Client.Forms;
using Dib4t.Contracts.Account;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Settings;

/// <summary>
/// The user dialog (legacy <c>settings/userModal.jsx</c>) and its client-side check, the legacy yup
/// <c>newUserSchema</c> / <c>editUserSchema</c> (<c>schemas/settings/userSchema.js</c>), with yup's messages.
/// </summary>
public sealed class UserForm
{
    /// <summary>The dialog's role checkboxes, in its order (<c>buildUser</c>); <c>canSeeAdjudicacoes</c> is UI-only (D15).</summary>
    public static IReadOnlyList<(string Role, string Label)> RoleCheckboxes { get; } =
    [
        (RoleNames.CanSeeOrcamentos, "Orçamentos"),
        (RoleNames.CanSeeObras, "Obras"),
        (Navigation.MenuItems.CanSeeAdjudicacoes, "Adjudicações"),
        (RoleNames.CanSeeEntidades, "Entidades"),
        (RoleNames.CanSeeDefinicoes, "Definições"),
        (RoleNames.CanSeeDashboard, "Dashboard"),
        (RoleNames.CanSeeLeituras, "Leituras"),
    ];

    private UserForm(string? id, string? title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>The user being edited; <c>null</c> for a new user.</summary>
    public string? Id { get; }

    public bool IsNew => Id is null;

    /// <summary>Legacy dialog title: <c>Editar user: {name}</c> or <c>Novo user</c>.</summary>
    public string? Title { get; }

    public string? Name { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public string? ConfirmPassword { get; set; }

    /// <summary>One of <see cref="FieldsOfCompetence.All"/>; <c>null</c> when the stored value is none of them.</summary>
    public string? FieldOfCompetencies { get; set; }

    /// <summary>Checked roles, by role name.</summary>
    public HashSet<string> Roles { get; } = new(StringComparer.Ordinal);

    public static UserForm New()
    {
        var form = new UserForm(null, "Novo user") { Name = string.Empty, Email = string.Empty, FieldOfCompetencies = FieldsOfCompetence.All[0] };
        return form;
    }

    /// <summary>The dialog for <paramref name="user"/>: its values, an empty password, its roles checked.</summary>
    public static UserForm Edit(UserListItemResponse user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var form = new UserForm(user.Id, $"Editar user: {user.Name}")
        {
            Name = user.Name ?? string.Empty,
            Email = user.Email ?? string.Empty,
            FieldOfCompetencies = FieldsOfCompetence.All.FirstOrDefault(f => f == user.FieldOfCompetencies),
        };
        foreach (var role in user.Roles.OfType<string>())
        {
            form.Roles.Add(role);
        }

        return form;
    }

    public UserFormErrors Validate()
    {
        var name = string.IsNullOrEmpty(Name) ? Yup.Required("name") : null;
        var email = string.IsNullOrEmpty(Email) ? Yup.Required("Email") : Yup.IsEmail(Email) ? null : "Email must be a valid email";
        var password = IsNew && string.IsNullOrEmpty(Password) ? Yup.Required("password") : null;
        // oneOf([ref("password"), null]): an untouched confirmation of an untouched password is equal ("" = "").
        var confirm = (ConfirmPassword ?? string.Empty) == (Password ?? string.Empty) ? null : "Tem de ser iguar a passwrod";
        return new UserFormErrors(name, email, password, confirm);
    }

    /// <summary>
    /// Legacy <c>buildUser</c>: one role slot per checkbox, <c>null</c> when unchecked (so the stored array has the
    /// same shape as the legacy app's), the password as typed (<c>""</c> on edit = unchanged).
    /// </summary>
    public UserRequest ToRequest() => new(
        Name ?? string.Empty,
        Email ?? string.Empty,
        Password ?? string.Empty,
        [.. RoleCheckboxes.Select(box => Roles.Contains(box.Role) ? box.Role : null)],
        FieldOfCompetencies);
}

/// <summary>Per-field messages shown under the inputs (legacy Formik helper text).</summary>
public sealed record UserFormErrors(string? Name, string? Email, string? Password, string? ConfirmPassword)
{
    public static UserFormErrors None { get; } = new(null, null, null, null);

    public bool Any => Name is not null || Email is not null || Password is not null || ConfirmPassword is not null;
}
