using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Domain.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Dib4t.Application.Tests.Entities;

/// <summary>Legacy <c>routes/entity.js</c>: the order of the checks, the texts and statuses, NFC, and the log events.</summary>
public sealed class EntitiesServiceTests
{
    private const string Id = "5f1e9a2b3c4d5e6f70819299";
    private const string Valid = """{"name":"Aços Lda.","fiscalNumber":"","address":"","zipCode":"","location":"Porto","paymentCondition":"30 dias","type":"Cliente"}""";

    private readonly IEntityRepository _repository = Substitute.For<IEntityRepository>();
    private readonly RecordingLogger<EntitiesService> _logger = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private EntitiesService Service => new(_repository, _logger);

    [Fact]
    public async Task Create_checks_joi_then_the_name_then_the_casts()
    {
        _repository.ExistsByNameAsync("Já Existe", Arg.Any<CancellationToken>()).Returns(true);
        _repository.InsertAsync(Arg.Any<EntityInput>(), Arg.Any<CancellationToken>()).Returns(Entity("Aços Lda."));

        var invalid = await Service.CreateAsync(Body("""{"name":"Já Existe","paymentCondition":"x","type":"Parceiro"}"""), Cancellation);
        var taken = await Service.CreateAsync(Body("""{"name":"Já Existe","fiscalNumber":{},"paymentCondition":"x","type":"Cliente"}"""), Cancellation);
        var refused = await Service.CreateAsync(Body("""{"name":"Nova","fiscalNumber":{},"paymentCondition":"x","type":"Cliente"}"""), Cancellation);
        var created = await Service.CreateAsync(Body(Valid), Cancellation);

        invalid.Error.Should().Be(Error.Validation("\"type\" must be one of [Cliente, Fornecedor, Cliente e Fornecedor]"));
        taken.Error.Should().Be(Error.Validation("Nome de entidade já registado"), "the name is checked before the model casts");
        refused.Error!.Message.Should().Be("Something failed.");
        refused.Error.Kind.Should().Be(ErrorKind.Failure);
        created.Value.Name.Should().Be("Aços Lda.");
        await _repository.Received(1).InsertAsync(
            new EntityInput("Aços Lda.", EntityText.Of(""), EntityText.Of(""), EntityText.Of(""), EntityText.Of("Porto"), "30 dias", "Cliente"),
            Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Cast to string failed", StringComparison.Ordinal));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message == $"Entity {Id} created: Aços Lda. (Cliente)");
    }

    [Fact]
    public async Task The_duplicate_check_and_the_stored_values_use_the_nfc_form()
    {
        _repository.InsertAsync(Arg.Any<EntityInput>(), Arg.Any<CancellationToken>()).Returns(Entity("Café"));

        await Service.CreateAsync(Body("{\"name\":\"Cafe\u0301\",\"location\":\"Se\u0301\",\"paymentCondition\":\"30 dias\",\"type\":\"Cliente\"}"), Cancellation);

        await _repository.Received(1).ExistsByNameAsync("Caf\u00e9", Arg.Any<CancellationToken>());
        await _repository.Received(1).InsertAsync(
            Arg.Is<EntityInput>(e => e.Name == "Caf\u00e9" && e.Location == EntityText.Of("S\u00e9") && !e.FiscalNumber.IsPresent),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_checks_joi_then_the_id_then_existence_then_the_casts()
    {
        _repository.FindByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Entity("Antes"));
        _repository.UpdateAsync(Id, Arg.Any<EntityInput>(), Arg.Any<CancellationToken>()).Returns(Entity("Aços Lda."));

        var invalid = await Service.UpdateAsync("abc", Body("{}"), Cancellation);
        var cast = await Service.UpdateAsync("abc", Body(Valid), Cancellation);
        var missing = await Service.UpdateAsync("0123456789abcdef01234567", Body(Valid), Cancellation);
        var refused = await Service.UpdateAsync(Id, Body("""{"name":"x","address":[1],"paymentCondition":"x","type":"Cliente"}"""), Cancellation);
        var saved = await Service.UpdateAsync(Id.ToUpperInvariant(), Body(Valid), Cancellation);

        invalid.Error.Should().Be(Error.Validation("\"name\" is required"), "Joi runs before the id cast");
        cast.Error.Should().Be(Error.Failure("Something failed."));
        missing.Error.Should().Be(Error.Validation("Entidade não existe na bd"));
        refused.Error!.Message.Should().Be("Something failed.");
        saved.Value.Name.Should().Be("Aços Lda.");
        await _repository.DidNotReceive().ExistsByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_of_an_entity_deleted_meanwhile_is_the_missing_text()
    {
        _repository.FindByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Entity("Antes"));

        var result = await Service.UpdateAsync(Id, Body(Valid), Cancellation);

        result.Error.Should().Be(Error.Validation("Entidade não existe na bd"));
    }

    [Fact]
    public async Task Get_casts_the_id_and_answers_the_legacy_400()
    {
        _repository.FindByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Entity("Aços"));

        (await Service.GetAsync("getAll2", Cancellation)).Error.Should().Be(Error.Failure("Something failed."));
        (await Service.GetAsync("0123456789abcdef01234567", Cancellation)).Error.Should().Be(Error.Validation("Entidade não encontrada"));
        (await Service.GetAsync(Id, Cancellation)).Value.Name.Should().Be("Aços");
    }

    [Fact]
    public async Task Search_refusals_are_the_legacy_500_with_the_reason_in_the_log()
    {
        _repository.SearchAsync(Arg.Any<EntitySearch>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<BusinessEntity>?)null);

        var unconvertible = await Service.SearchAsync(Body("""{"name":{"toString":1}}"""), Cancellation);
        var invalid = await Service.SearchAsync(Body("""{"name":"("}"""), Cancellation);

        unconvertible.Error.Should().Be(Error.Failure("Something failed.") with { Reason = "name: Cannot convert object to primitive value" });
        invalid.Error.Should().Be(Error.Failure("Something failed."));
        _logger.Entries.Where(e => e.EventId == 64).Select(e => e.Message).Should().Equal(
            "Entity search failed (legacy 500): name: Cannot convert object to primitive value",
            "Entity search failed (legacy 500): MongoDB refused a pattern: name /^(/i, fiscalNumber /^undefined/i, location /^undefined/i, address /^undefined/i, zipCode /^undefined/i");
    }

    [Fact]
    public async Task Search_sends_the_five_patterns_in_the_legacy_order()
    {
        _repository.SearchAsync(Arg.Any<EntitySearch>(), Arg.Any<CancellationToken>()).Returns([Entity("Metalúrgica")]);

        var found = await Service.SearchAsync(Body("""{"name":"Met","nif":"","localidade":"Por","morada":"","codigoPostal":""}"""), Cancellation);

        found.Value.Should().ContainSingle();
        var sent = (EntitySearch)_repository.ReceivedCalls().Single().GetArguments()[0]!;
        sent.Patterns.Should().Equal(("name", "^Met"), ("fiscalNumber", "^"), ("location", "^Por"), ("address", "^"), ("zipCode", "^"));
    }

    [Fact]
    public void Clients_are_the_two_client_types() =>
        new[] { "Cliente", "Fornecedor", "Cliente e Fornecedor", "cliente", null }
            .Select(type => Entity("x") with { Type = type }).Select(e => e.IsClient)
            .Should().Equal(true, false, true, false, false);

    private static BusinessEntity Entity(string name) => new(Id, name, null, null, null, "Porto", "30 dias", "Cliente", 0);

    private static JsonElement Body(string json) => JsonElement.Parse(json);
}
