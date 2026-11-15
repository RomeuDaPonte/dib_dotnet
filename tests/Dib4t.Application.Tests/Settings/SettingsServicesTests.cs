using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Application.Settings;
using Dib4t.Domain.Settings;
using Dib4t.Domain.Users;
using NSubstitute;

namespace Dib4t.Application.Tests.Settings;

/// <summary>Legacy prices, payment-method and printer routes and the startup seed: order of checks and texts.</summary>
public sealed class SettingsServicesTests
{
    private const string Id = "5f1e9a2b3c4d5e6f70819299";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Prices_update_without_a_prices_document_is_the_legacy_500()
    {
        var repository = Substitute.For<IPricesRepository>();
        var service = new PricesService(repository, new RecordingLogger<PricesService>());

        var invalid = await service.UpdateAsync(Body("""{"margin":0}"""), Cancellation);
        var missing = await service.UpdateAsync(Body(Prices()), Cancellation);
        repository.UpdateAsync(Arg.Any<Prices>(), Arg.Any<CancellationToken>()).Returns(true);
        var saved = await service.UpdateAsync(Body(Prices(automation: "\"32.5\"")), Cancellation);

        invalid.Error.Should().Be(Error.Validation("\"automation\" is required"));
        missing.Error.Should().Be(Error.Failure("Something failed."));
        saved.Value.Should().Be(new Prices(32.5, 45, 30, 38, 28, 35), "a numeric text is stored as its number");
    }

    [Fact]
    public async Task Payment_methods_check_the_name_and_the_id_like_legacy()
    {
        var repository = Substitute.For<IPaymentMethodRepository>();
        repository.ExistsAsync("Cheque", Arg.Any<CancellationToken>()).Returns(true);
        repository.InsertAsync("Transferência", Arg.Any<CancellationToken>()).Returns(new PaymentMethod(Id, "Transferência", 0));
        repository.FindByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(new PaymentMethod(Id, "Cheque", 0));
        repository.DeleteAsync(Id, Arg.Any<CancellationToken>()).Returns(new PaymentMethod(Id, "Cheque", 0));
        var service = new PaymentMethodsService(repository, new RecordingLogger<PaymentMethodsService>());

        (await service.AddAsync(Body("{}"), Cancellation)).Error.Should().Be(Error.Validation("\"name\" is required"));
        (await service.AddAsync(Body("""{"name":"Cheque"}"""), Cancellation)).Error.Should().Be(Error.Validation("Modo de pagamento com esse nome já está registado"));
        (await service.AddAsync(Body("""{"name":"Transferência"}"""), Cancellation)).Error.Should().BeNull("an NFD name is stored as NFC");
        (await service.DeleteAsync("abc", Cancellation)).Error.Should().Be(Error.Failure("Something failed."));
        (await service.DeleteAsync("0123456789abcdef01234567", Cancellation)).Error.Should().Be(Error.Validation("Modo de pagamento não existe na base de dados"));
        (await service.DeleteAsync(Id.ToUpperInvariant(), Cancellation)).Value.Should().Be(new PaymentMethod(Id, "Cheque", 0));

        repository.DeleteAsync(Id, Arg.Any<CancellationToken>()).Returns((PaymentMethod?)null);
        (await service.DeleteAsync(Id, Cancellation)).Error.Should().Be(Error.Failure("Erro no servidor!!"));
    }

    [Fact]
    public async Task Printers_check_the_id_before_the_body_and_answer_404()
    {
        var repository = Substitute.For<IPrinterSettingsRepository>();
        var service = new PrinterSettingsService(repository, new RecordingLogger<PrinterSettingsService>());
        var valid = Body("""{"ip":"127.0.0.1","port":"9100","templateName":"producao"}""");

        (await service.UpdateAsync("abc", Body("{}"), Cancellation)).Error!.Message.Should().Be("\"id\" with value \"abc\" fails to match the valid mongo id pattern");
        (await service.UpdateAsync(Id, Body("{}"), Cancellation)).Error!.Message.Should().Be("\"ip\" is required");
        (await service.UpdateAsync(Id, valid, Cancellation)).Error.Should().Be(Error.NotFound("Printer settings not found."));
        (await service.DeleteAsync(Id, Cancellation)).Error.Should().Be(Error.NotFound("Printer settings not found."));
        (await service.ListByTemplateAsync("outro", Cancellation)).Error.Should().Be(Error.NotFound("Template settings not found."));
        await repository.Received(1).UpdateAsync(Id, new PrinterInput("127.0.0.1", 9100, "producao"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seeding_creates_the_legacy_defaults_only_when_missing()
    {
        var prices = Substitute.For<IPricesRepository>();
        var users = Substitute.For<IUserRepository>();
        var seeder = new DefaultDataSeeder(prices, users, new RecordingLogger<DefaultDataSeeder>());

        await seeder.SeedAsync(Cancellation);
        prices.FindAsync(Arg.Any<CancellationToken>()).Returns(Domain.Settings.Prices.Defaults);
        users.FindByEmailAsync("userlogado@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new User(Id, "User Logado", "userlogado@gmail.com", "x", [], "Desenvolvimento"));
        await seeder.SeedAsync(Cancellation);

        await prices.Received(1).InsertAsync(new Prices(10, 10, 10, 10, 10, 35), Arg.Any<CancellationToken>());
        await users.Received(1).InsertAsync(
            Arg.Is<NewUser>(u => u.Name == "User Logado" && u.Email == "userlogado@gmail.com"
                && u.PasswordHash == "$2b$10$fSMIGpdKqJ8Eqmkf4NAWEu0hGnkNRaexlLWKeI0kSuMFdnrryjUKy"
                && u.Roles.SequenceEqual(new[] { "canSeeDefinicoes", "canSeeEntidades", "canSeeOrcamentos", "canSeeObras", "canSeeHistorico", "canSeeDashboard" })
                && u.FieldOfCompetencies == "Desenvolvimento"),
            Arg.Any<CancellationToken>());
    }

    private static JsonElement Body(string json) => JsonElement.Parse(json);

    private static string Prices(string automation = "32.5") =>
        $$"""{"automation":{{automation}},"consultancy":45,"development":30,"machining":38,"assembly":28,"margin":35}""";
}
