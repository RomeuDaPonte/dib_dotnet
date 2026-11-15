using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Settings;
using Dib4t.Application.Tests.Common;

namespace Dib4t.Application.Tests.Settings;

/// <summary>
/// The legacy users / prices / printers / payment-method contract, case by case from the legacy code's own answers
/// (<c>settings-golden.json</c>): the same Joi 400 text, the same refusal (500) where the Mongoose model refuses a
/// body Joi accepted, and the same values to store. (The saves themselves are checked on MongoDB in
/// <c>Dib4t.Infrastructure.Tests</c>.)
/// </summary>
public sealed class SettingsGoldenTests
{
    public static TheoryData<int, string> CreateUserBodies => SettingsGolden.Rows("createUser");

    public static TheoryData<int, string> EditUserBodies => SettingsGolden.Rows("editUser");

    public static TheoryData<int, string> PricesBodies => SettingsGolden.Rows("prices");

    public static TheoryData<int, string> PrinterBodies => SettingsGolden.Rows("createPrinter");

    public static TheoryData<int, string> PrinterUpdates => SettingsGolden.Rows("editPrinter");

    public static TheoryData<int, string> PaymentMethodBodies => SettingsGolden.Rows("createPaymentMethod");

    [Theory]
    [MemberData(nameof(CreateUserBodies))]
    public void Create_user_answers_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("createUser", index);

        var validated = UserValidators.ValidateCreate(JsonElement.Parse(body));

        if (expected.Message() is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        validated.IsSuccess.Should().BeTrue(body);
        var input = validated.Value.ToInput();
        if (expected.Outcome() == "500")
        {
            input.Error!.Kind.Should().Be(ErrorKind.Failure, body);
            input.Error.Message.Should().Be(LegacyMessages.SomethingFailed);
            return;
        }

        var stored = expected.GetProperty("stored");
        input.Value.Name.Should().Be(stored.GetProperty("name").GetString());
        input.Value.Email.Should().Be(stored.GetProperty("email").GetString());
        input.Value.FieldOfCompetencies.Should().Be(stored.GetProperty("fieldOfCompetencies").GetString());
        input.Value.Roles.Should().Equal(stored.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        input.Value.Password.Text.Should().Be(JsonElement.Parse(body).GetProperty("password").GetString());
    }

    [Theory]
    [MemberData(nameof(EditUserBodies))]
    public void Edit_user_validates_and_refuses_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("editUser", index);

        var validated = UserValidators.ValidateEdit(JsonElement.Parse(body));

        if (expected.Message() is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        var input = validated.Value.ToInput();
        var refused = !input.IsSuccess || input.Value.Password.IllegalType is not null;
        refused.Should().Be(expected.Outcome() == "500", body);
        if (!refused)
        {
            var hashes = expected.GetProperty("update").ValueKind != JsonValueKind.Null
                && expected.GetProperty("update").TryGetProperty("$set", out var set) && set.TryGetProperty("password", out _);
            (input.Value.Password.Text is not null).Should().Be(hashes, "a password is hashed only when the legacy save writes one: " + body);
        }
    }

    [Theory]
    [MemberData(nameof(PricesBodies))]
    public void Prices_validate_and_convert_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("prices", index);

        var validated = SettingsValidators.ValidatePrices(JsonElement.Parse(body));

        if (expected.Message() is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        var response = expected.GetProperty("response");
        var prices = validated.Value;
        new[] { prices.Automation, prices.Consultancy, prices.Development, prices.Machining, prices.Assembly, prices.Margin }
            .Should().Equal(
                new[] { "automation", "consultancy", "development", "machining", "assembly", "margin" }
                    .Select(key => (double?)response.GetProperty(key).GetDouble()),
                body);
    }

    [Theory]
    [MemberData(nameof(PrinterBodies))]
    public void Printer_settings_validate_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("createPrinter", index);

        var validated = SettingsValidators.ValidatePrinter(JsonElement.Parse(body));

        if (expected.Message() is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
            return;
        }

        var response = expected.GetProperty("response");
        validated.Value.Should().Be(new PrinterInput(
            response.GetProperty("ip").GetString()!, response.GetProperty("port").GetDouble(), response.GetProperty("templateName").GetString()!), body);
    }

    [Theory]
    [MemberData(nameof(PrinterUpdates))]
    public void Printer_ids_are_checked_before_the_body_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("editPrinter", index);
        var id = expected.GetProperty("id").GetString()!;

        var message = SettingsValidators.ValidatePrinterId(id) ?? SettingsValidators.ValidatePrinter(JsonElement.Parse(body)).Error?.Message;

        message.Should().Be(expected.Message(), $"{id} {body}");
    }

    [Theory]
    [MemberData(nameof(PaymentMethodBodies))]
    public void Payment_methods_validate_like_the_legacy_code(int index, string body)
    {
        var expected = SettingsGolden.Case("createPaymentMethod", index);

        var validated = SettingsValidators.ValidatePaymentMethod(JsonElement.Parse(body));

        if (expected.Message() is { } message)
        {
            validated.Error.Should().Be(Error.Validation(message), body);
        }
        else
        {
            validated.Value.Should().Be(expected.GetProperty("stored").GetProperty("name").GetString(), "names are stored as sent (never trimmed)");
        }
    }

    [Fact]
    public void Path_ids_cast_like_mongoose_findById()
    {
        foreach (var item in SettingsGolden.Root.GetProperty("pathIds").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expected = item.GetProperty("cast").GetString() == "ok" ? item.GetProperty("objectId").GetString() : null;

            LegacyObjectId.Cast(id).Should().Be(expected, id);
        }
    }

    [Fact]
    public void The_ip_pattern_is_the_one_of_the_legacy_joi() =>
        JoiIp.JavaScriptSource.Should().Be(SettingsGolden.Root.GetProperty("ipPattern").GetString());

    [Fact]
    public void Roles_are_the_legacy_getAllRoles() =>
        UserAdministrationService.Roles.Should().Equal(SettingsGolden.Root.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
}
