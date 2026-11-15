using System.Text.Json;
using Dib4t.Client.Auth;
using Dib4t.Client.Navigation;
using Dib4t.Tests.Common;

namespace Dib4t.Client.Tests.Auth;

public sealed class SessionUserAndFormTests
{
    [Fact]
    public void Reads_the_claims_of_a_token_made_by_the_legacy_library()
    {
        var token = JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)).GetProperty("jwt").GetProperty("cases")[0].GetProperty("token").GetString();

        var user = SessionUser.FromToken(token);

        user.Should().NotBeNull();
        user!.Id.Should().Be("5f1e9a2b3c4d5e6f70819203");
        user.Name.Should().Be("Ana Administradora");
        user.Roles.Should().Equal("canSeeOrcamentos", "canSeeDefinicoes"); // the null element is skipped
        user.ToPrincipal().IsInRole("canSeeDefinicoes").Should().BeTrue();
        user.ToPrincipal().Identity!.Name.Should().Be("Ana Administradora");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("a.e30.c")] // {} : no _id
    [InlineData("a.WzFd.c")] // [1]
    public void An_unreadable_token_is_no_user(string? token) => SessionUser.FromToken(token).Should().BeNull();

    [Theory]
    [InlineData(null, null, "Email is a required field", "password is a required field")]
    [InlineData("abc", "12345", "Email must be a valid email", null)]
    [InlineData("a@b", "12345", "Email must be a valid email", null)]
    [InlineData("ana.admin@dib4t.local\n", "12345", "Email must be a valid email", null)]
    [InlineData("ana.admin@dib4t.local", "1234", null, "password must be at least 5 characters")]
    [InlineData("ana.admin@dib4t.local", "12345", null, null)]
    [InlineData("joão.ção@dib4t.pt", "😀😀a", null, null)]
    [InlineData("\"quoted\"@dib4t.pt", "12345", null, null)]
    public void Login_form_checks_like_the_legacy_yup_schema(string? email, string? password, string? emailError, string? passwordError) =>
        new LoginForm { Email = email, Password = password }.Validate().Should().Be(new LoginFormErrors(emailError, passwordError));

    [Fact]
    public void Menu_follows_the_roles_in_the_legacy_order()
    {
        MenuItems.For(["canSeeDefinicoes", "canSeeObras", "canSeeAdjudicacoes", "canSeeHistorico"]).Select(i => i.Label)
            .Should().Equal("Obras", "Adjudicações", "Definições");
        MenuItems.All.Select(i => i.Path).Should().Equal(
            "landing/dashboard", "landing/budgets", "landing/job", "landing/jobReceivings", "landing/entity", "landing/leituras", "landing/settings");
    }
}
