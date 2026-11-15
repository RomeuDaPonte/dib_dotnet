using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Tests.Common;

namespace Dib4t.Application.Tests.Account;

/// <summary>Legacy <c>validateLogin</c> (Joi 14): the same 400 text as the legacy code for every body of the golden file.</summary>
public sealed class LoginValidatorTests
{
    public static TheoryData<string, string?> GoldenBodies
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var (body, message) in AuthGolden.LoginValidation)
            {
                data.Add(body, message);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenBodies))]
    public void Gives_the_legacy_message(string body, string? message)
    {
        var result = LoginValidator.Validate(JsonElement.Parse(body));

        if (message is null)
        {
            result.IsSuccess.Should().BeTrue("legacy accepts {0}", body);
        }
        else
        {
            result.IsSuccess.Should().BeFalse("legacy rejects {0}", body);
            result.Error!.Message.Should().Be(message);
        }
    }

    [Fact]
    public void A_duplicate_key_takes_the_last_value()
    {
        var result = LoginValidator.Validate(JsonElement.Parse("""{"email":1,"password":"first-pass","email":"a@b.pt","password":"last-pass"}"""));

        result.Value.Should().Be(new LoginCredentials("a@b.pt", "last-pass"));
    }

    [Fact]
    public void Credentials_never_print_the_password() =>
        new LoginCredentials("a@b.pt", "secret-pass").ToString().Should().NotContain("secret-pass");
}
