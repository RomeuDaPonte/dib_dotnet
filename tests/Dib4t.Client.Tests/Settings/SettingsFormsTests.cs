using Dib4t.Client.Forms;
using Dib4t.Client.Settings;
using Dib4t.Contracts.Account;
using Dib4t.Contracts.Settings;

namespace Dib4t.Client.Tests.Settings;

/// <summary>
/// The settings forms' client-side checks, ports of the legacy yup 0.29.3 schemas. The expected texts are what the
/// legacy front-end's own yup answers for the same values (run in Node against <c>front-end/node_modules/yup</c>).
/// </summary>
public sealed class SettingsFormsTests
{
    [Theory]
    [InlineData("10", 10d)]
    [InlineData(" 32.5 ", 32.5)]
    [InlineData("1e2", 100d)]
    [InlineData(".5", 0.5)]
    [InlineData("0x10", 16d)]
    [InlineData("0b11", 3d)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("1 000", 1000d)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("32,5", null)]
    [InlineData("12abc", null)]
    [InlineData("0b12", null)]
    public void Yup_number_casts_like_javascript(string text, double? expected) => Yup.Number(text).Should().Be(expected);

    [Fact]
    public void A_new_user_needs_name_email_and_password_with_the_yup_messages()
    {
        var errors = UserForm.New().Validate();

        errors.Should().Be(new UserFormErrors("name is a required field", "Email is a required field", "password is a required field", null));
    }

    [Fact]
    public void An_edit_keeps_the_password_optional_and_checks_the_confirmation()
    {
        var form = UserForm.Edit(new UserListItemResponse(["canSeeObras", null], "Maquinação", "5f1e9a2b3c4d5e6f70819204", "Bruno", "x"));

        form.Validate().Should().Be(new UserFormErrors(null, "Email must be a valid email", null, null));
        form.Email = "bruno@dib4t.local";
        form.Password = "p";
        form.Validate().ConfirmPassword.Should().Be("Tem de ser iguar a passwrod");
        form.ConfirmPassword = "p";
        form.Validate().Any.Should().BeFalse();
    }

    [Fact]
    public void The_request_has_one_role_slot_per_checkbox_like_the_legacy_dialog()
    {
        var form = UserForm.Edit(new UserListItemResponse(["canSeeObras", null, "canSeeLeituras", "canSeeHistorico"], "Montagem", "id", "Bruno", "bruno@dib4t.local"));
        form.Roles.Add("canSeeAdjudicacoes");

        var request = form.ToRequest();

        request.Roles.Should().Equal(null, "canSeeObras", "canSeeAdjudicacoes", null, null, null, "canSeeLeituras");
        request.Password.Should().BeEmpty("an empty password on edit means unchanged");
        request.FieldOfCompetencies.Should().Be("Montagem");
        form.Title.Should().Be("Editar user: Bruno");
        UserForm.New().Title.Should().Be("Novo user");
    }

    [Fact]
    public void Prices_are_edited_as_texts_with_the_yup_messages()
    {
        var form = PricesForm.From(new PricesResponse(32.5, 45, 30, 38, 28, 35));
        form.Values["automation"].Should().Be("32.5");
        form.Values["margin"] = "32,5";
        form.Values["assembly"] = string.Empty;

        form.Validate().Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["margin"] = "margin must be a `number` type, but the final value was: `NaN` (cast from the value `\"32,5\"`).",
            ["assembly"] = "assembly must be a `number` type, but the final value was: `NaN` (cast from the value `\"\"`).",
        });
        form.Values["margin"] = "35";
        form.Values["assembly"] = " 28 ";
        form.ToRequest().Should().Be(new PricesRequest("32.5", "45", "30", "38", "35", " 28 "), "sent as typed, like the legacy Formik form");
    }

    [Fact]
    public void Printers_check_ipv4_port_and_template_with_the_yup_messages()
    {
        var empty = PrinterForm.New().Validate();
        var edit = PrinterForm.Edit(new PrinterSettingsResponse("id", "::1", 9100, "producao", 0));

        empty.Should().Be(new PrinterFormErrors("IP inválido", "Porta must be a `number` type, but the final value was: `NaN` (cast from the value `\"\"`).",
            "Template must be one of the following values: producao, maquinacao"));
        edit.Validate().Should().Be(new PrinterFormErrors("IP inválido", null, null), "the dialog only accepts IPv4 (the API accepts more)");
        edit.Ip = "010.1.1.1";
        edit.Validate().Any.Should().BeFalse();
        edit.ToRequest().Should().Be(new PrinterSettingsRequest("010.1.1.1", 9100, "producao"));
    }
}
