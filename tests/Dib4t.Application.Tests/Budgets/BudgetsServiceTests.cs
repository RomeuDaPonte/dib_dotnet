using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Budgets;

/// <summary>
/// Legacy <c>routes/budget.js</c> (list, search, get, revisions, create, clone, new revision, edit): the order of the
/// checks, the texts and statuses (the supplier's 200 notice included), the values written, the numbering (year in the
/// configured zone, collision guard), the decided fixes D6, D7, D9, D21, and the log events.
/// </summary>
public sealed class BudgetsServiceTests
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810001";
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";
    private const string SupplierId = "5f1e9a2b3c4d5e6f70820002";
    private const string UserId = "5f1e9a2b3c4d5e6f70830001";
    private const string Unknown = "5f1e9a2b3c4d5e6f708399ff";

    private static readonly BusinessEntity Client = new(ClientId, "Metalúrgica", "509", "Rua", "4000", "Porto", "30 dias", "Cliente", 0);
    private static readonly BusinessEntity Supplier = Client with { Id = SupplierId, Type = "Fornecedor" };
    private static readonly BudgetAuthor Author = new(UserId, "Ana", "ana@dib4t.local", ["canSeeOrcamentos"], "Desenvolvimento", 0);

    private readonly IBudgetRepository _budgets = Substitute.For<IBudgetRepository>();
    private readonly IBudgetTaskRepository _tasks = Substitute.For<IBudgetTaskRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IPricesRepository _prices = Substitute.For<IPricesRepository>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 15, 10, 20, 30, 123, TimeSpan.Zero).AddTicks(4567));
    private readonly RecordingLogger<BudgetsService> _logger = new();
    private readonly AppOptions _options = new();

    public BudgetsServiceTests()
    {
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(Client);
        _entities.FindByIdAsync(SupplierId, Arg.Any<CancellationToken>()).Returns(Supplier);
        _budgets.FindAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns(Author);
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored());
        _budgets.CountNumbersSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(6);
        _budgets.InsertAsync(Arg.Any<NewBudget>(), Arg.Any<CancellationToken>()).Returns(call => FromNew(call.Arg<NewBudget>()));
        _prices.FindAsync(Arg.Any<CancellationToken>()).Returns(new Prices(32.5, 10, 10, 10, 10, 35));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private BudgetsService Service => new(_budgets, _tasks, _entities, _prices, new BudgetNumbering(new RecordingLogger<BudgetNumbering>()),
        _time, Options.Create(_options), _logger);

    [Fact]
    public async Task Create_checks_joi_the_slash_the_client_the_supplier_the_author_then_the_prices()
    {
        var joi = await Service.CreateAsync(Body(UserId, ClientId, descriptive: ""), Cancellation);
        var slash = await Service.CreateAsync(Body(Unknown, Unknown, descriptive: "Suporte 30/40"), Cancellation);
        var client = await Service.CreateAsync(Body(Unknown, Unknown), Cancellation);
        var supplier = await Service.CreateAsync(Body(Unknown, SupplierId), Cancellation);
        var author = await Service.CreateAsync(Body(Unknown, ClientId), Cancellation);
        _prices.FindAsync(Arg.Any<CancellationToken>()).Returns((Prices?)null);
        var noPrices = await Service.CreateAsync(Body(UserId, ClientId), Cancellation);

        joi.Error.Should().Be(Error.Validation("\"descriptive\" is not allowed to be empty"));
        slash.Error.Should().Be(Error.Validation(BudgetMessages.DescriptiveWithSlash), "D21, before any lookup");
        client.Error.Should().Be(Error.Validation(BudgetMessages.ClientMissing));
        supplier.Value.Should().Be(BudgetSaved.Refused(BudgetMessages.ClientIsSupplier), "legacy answers 200 with this text, before the author check");
        author.Error.Should().Be(Error.Validation(BudgetMessages.AuthorMissing));
        noPrices.Error!.Kind.Should().Be(ErrorKind.Failure);
        noPrices.Error.Message.Should().Be(LegacyMessages.SomethingFailed);
        await _budgets.DidNotReceive().InsertAsync(Arg.Any<NewBudget>(), Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 75 && e.Message.Contains("supplier", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_numbers_prices_and_dates_the_budget_then_adds_the_default_task()
    {
        var created = await Service.CreateAsync(Body(UserId.ToUpperInvariant(), ClientId, descriptive: "Máquina", person: "Eng.ª Maria"), Cancellation);

        var details = created.Value.Budget!;
        details.Client.Should().Be(Client);
        details.MadeBy.Should().Be(Author);
        await _budgets.Received(1).CountNumbersSinceAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
        await _budgets.Received(1).InsertAsync(
            new NewBudget(0, ClientId, "Máquina", "Eng.ª Maria", UserId, "26-0007", "30 dias", 35, 43.5, 60, 30, null,
                new DateTime(2026, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
        await _tasks.Received(1).InsertDefaultAsync(details.Budget.Id, 32.5, Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 70 && e.Message.Contains("26-0007 R0 Máquina for Metalúrgica", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_without_a_payment_condition_is_refused_like_the_legacy_save()
    {
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(Client with { PaymentCondition = "" });

        var created = await Service.CreateAsync(Body(UserId, ClientId), Cancellation);

        created.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        created.Error.Reason.Should().Contain("paymentMethod");
        await _budgets.DidNotReceive().InsertAsync(Arg.Any<NewBudget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_numbering_skips_numbers_in_use_and_takes_the_year_in_the_configured_zone()
    {
        _budgets.NumberExistsAsync("26-0007", Arg.Any<CancellationToken>()).Returns(true);
        _budgets.NumberExistsAsync("26-0008", Arg.Any<CancellationToken>()).Returns(true);

        await Service.CreateAsync(Body(UserId, ClientId), Cancellation);

        await _budgets.Received(1).InsertAsync(Arg.Is<NewBudget>(b => b.BudgetNumber == "26-0009"), Arg.Any<CancellationToken>());

        _time.SetUtcNow(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero));
        _budgets.CountNumbersSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
        await Service.CreateAsync(Body(UserId, ClientId), Cancellation);
        _options.TimeZone = "Asia/Tokyo"; // already 1 January 2027 there
        await Service.CreateAsync(Body(UserId, ClientId), Cancellation);

        await _budgets.Received(1).InsertAsync(Arg.Is<NewBudget>(b => b.BudgetNumber == "26-0001"), Arg.Any<CancellationToken>());
        await _budgets.Received(1).InsertAsync(Arg.Is<NewBudget>(b => b.BudgetNumber == "27-0001"), Arg.Any<CancellationToken>());
        await _budgets.Received(1).CountNumbersSinceAsync(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrent_creates_get_different_numbers()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        _budgets.CountNumbersSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(_ => taken.Count);
        _budgets.NumberExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => taken.Contains(call.Arg<string>()));
        _budgets.InsertAsync(Arg.Any<NewBudget>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(5, Cancellation); // the window where legacy hands out the same number twice
            lock (taken)
            {
                taken.Add(call.Arg<NewBudget>().BudgetNumber);
            }

            return FromNew(call.Arg<NewBudget>());
        });
        var service = Service; // one numbering lock, as the singleton in the app

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CreateAsync(Body(UserId, ClientId), Cancellation)));

        taken.Order(StringComparer.Ordinal).Should().Equal(Enumerable.Range(1, 8).Select(n => $"26-{n:D4}"));
    }

    [Fact]
    public async Task Clone_checks_joi_then_the_source_then_copies_its_values_and_tasks_under_a_new_number()
    {
        IReadOnlyList<BudgetTask> sourceTasks = [new("t1", BudgetId, "automation", "", 32.5, 1, 0)];
        _tasks.ListByBudgetAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(sourceTasks);

        var joi = await Service.CloneAsync("not-an-id", """{"clientId":"x"}""".AsJson(), Cancellation);
        var cast = await Service.CloneAsync("not-an-id", Body(UserId, ClientId), Cancellation);
        var missing = await Service.CloneAsync(Unknown, Body(UserId, Unknown), Cancellation);
        var supplier = await Service.CloneAsync(BudgetId, Body(UserId, SupplierId), Cancellation);
        var cloned = await Service.CloneAsync(BudgetId, Body(UserId, ClientId, descriptive: "Cópia", person: "Sr. Paulo"), Cancellation);

        joi.Error!.Message.Should().StartWith("\"clientId\" with value \"x\"");
        cast.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        missing.Error.Should().Be(Error.Validation(BudgetMessages.Missing), "the budget is looked up before the client");
        supplier.Value.Notice.Should().Be(BudgetMessages.ClientIsSupplier);
        var source = Stored();
        await _budgets.Received(1).InsertAsync(
            new NewBudget(0, ClientId, "Cópia", "Sr. Paulo", UserId, "26-0007", source.PaymentMethod!, source.Margin, source.Total,
                source.DaysNeededToDoTheWork, source.Validity, source.Description, new DateTime(2026, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
        await _tasks.Received(1).InsertCopiesAsync(cloned.Value.Budget!.Budget.Id, sourceTasks, Arg.Any<CancellationToken>());
        await _tasks.DidNotReceive().InsertDefaultAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_revision_takes_the_number_of_documents_as_its_revision_and_copies_everything()
    {
        _budgets.CountRevisionsAsync("26-0003", Arg.Any<CancellationToken>()).Returns(2);
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(Client);

        var missing = await Service.CreateRevisionAsync(Unknown, Cancellation);
        var revision = await Service.CreateRevisionAsync(BudgetId, Cancellation);

        missing.Error.Should().Be(Error.Validation(BudgetMessages.Missing));
        var source = Stored();
        await _budgets.Received(1).InsertAsync(
            new NewBudget(2, ClientId, source.Descriptive, source.PersonInCharge, UserId, "26-0003", source.PaymentMethod!, source.Margin,
                source.Total, source.DaysNeededToDoTheWork, source.Validity, source.Description, new DateTime(2026, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
        revision.Value.Client.Should().Be(Client);
        revision.Value.MadeBy.Should().Be(Author);
        _logger.Entries.Should().Contain(e => e.EventId == 72 && e.Message.Contains("revision 2 of 26-0003", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_validates_d7_then_the_id_the_budget_the_author_the_job_and_the_d21_rule()
    {
        _budgets.UpdateAsync(BudgetId, Arg.Any<BudgetEdit>(), Arg.Any<CancellationToken>()).Returns(call => Stored() with { Descriptive = call.Arg<BudgetEdit>().Descriptive });

        var d7 = await Service.UpdateAsync(BudgetId, """{"descriptive":"x","personInCharge":"y"}""".AsJson(), Cancellation);
        var cast = await Service.UpdateAsync("x", Edit(UserId, "x"), Cancellation);
        var missing = await Service.UpdateAsync(Unknown, Edit(UserId, "x"), Cancellation);
        var author = await Service.UpdateAsync(BudgetId, Edit(Unknown, "x"), Cancellation);
        var slash = await Service.UpdateAsync(BudgetId, Edit(UserId, "Novo 1/2"), Cancellation);
        var saved = await Service.UpdateAsync(BudgetId, Edit(UserId, "Novo"), Cancellation);

        d7.Error.Should().Be(Error.Validation("\"madeById\" is required"), "D7: legacy skips the validation and fails on the lookup");
        cast.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        missing.Error.Should().Be(Error.Validation(BudgetMessages.Missing));
        author.Error.Should().Be(Error.Validation(BudgetMessages.AuthorMissing));
        slash.Error.Should().Be(Error.Validation(BudgetMessages.DescriptiveWithSlash));
        saved.Value.Budget.Descriptive.Should().Be("Novo");
        saved.Value.Client.Should().Be(Client);
        await _budgets.Received(1).UpdateAsync(BudgetId, new BudgetEdit(UserId, "Novo", "Eng.ª Maria"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_existing_slash_may_stay_and_a_budget_with_a_job_is_answered_unchanged()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { Descriptive = "Suporte 30/40" });
        _budgets.UpdateAsync(BudgetId, Arg.Any<BudgetEdit>(), Arg.Any<CancellationToken>()).Returns(Stored());

        var kept = await Service.UpdateAsync(BudgetId, Edit(UserId, "Suporte 30/40"), Cancellation);
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { HasGenerateJob = true });
        var locked = await Service.UpdateAsync(BudgetId, Edit(UserId, "Outro"), Cancellation);

        kept.IsSuccess.Should().BeTrue("D21 refuses only a changed descriptive");
        locked.Value.Budget.Descriptive.Should().Be("Máquina de corte");
        await _budgets.Received(1).UpdateAsync(Arg.Any<string>(), Arg.Any<BudgetEdit>(), Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 74);
    }

    [Fact]
    public async Task List_and_search_keep_the_latest_revision_of_each_number()
    {
        IReadOnlyList<BudgetRow> rows =
        [
            new(Stored() with { Id = "r2", Revision = 2 }, "C", "A"),
            new(Stored() with { Id = "r1", Revision = 1 }, "C", "A"),
            new(Stored() with { Id = "other", BudgetNumber = "26-0002" }, "C", "A"),
        ];
        _budgets.ListNewestAsync(200, Arg.Any<CancellationToken>()).Returns(rows);
        _budgets.SearchAsync("corti", Arg.Any<CancellationToken>()).Returns((rows, (string?)null));

        var list = await Service.ListAsync(Cancellation);
        var found = await Service.SearchAsync("""{"q":"corti"}""".AsJson(), Cancellation);

        list.Select(r => r.Budget.Id).Should().Equal("r2", "other");
        found.Value.Select(r => r.Budget.Id).Should().Equal("r2", "other");
    }

    [Fact]
    public async Task Search_refusals_are_400_with_the_reason_d9()
    {
        _budgets.SearchAsync("((", Arg.Any<CancellationToken>()).Returns(((IReadOnlyList<BudgetRow>?)null, "Regular expression is invalid: missing closing parenthesis"));

        var joi = await Service.SearchAsync("""{"q":"a"}""".AsJson(), Cancellation);
        var invalid = await Service.SearchAsync("""{"q":"(("}""".AsJson(), Cancellation);
        var nul = await Service.SearchAsync("{\"q\":\"a\\u0000b\"}".AsJson(), Cancellation);

        joi.Error.Should().Be(Error.Validation("\"q\" length must be at least 2 characters long"));
        invalid.Error.Should().Be(Error.Validation("Error performing search: Regular expression is invalid: missing closing parenthesis"));
        nul.Error.Should().Be(Error.Validation("Error performing search: value a\0b must not contain null bytes"), "the legacy driver's text");
        await _budgets.DidNotReceive().SearchAsync("a\0b", Arg.Any<CancellationToken>());
        _logger.Entries.Should().Contain(e => e.EventId == 78 && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Get_casts_the_id_then_answers_the_populated_budget()
    {
        var cast = await Service.GetAsync("abc", Cancellation);
        var missing = await Service.GetAsync(Unknown, Cancellation);
        var found = await Service.GetAsync(BudgetId.ToUpperInvariant(), Cancellation);

        cast.Error!.Message.Should().Be(LegacyMessages.SomethingFailed);
        _logger.Entries.Should().Contain(e => e.EventId == 77);
        missing.Error.Should().Be(Error.Validation(BudgetMessages.NotFound));
        found.Value.Should().Be(new BudgetDetails(Stored(), Client, Author));
    }

    [Fact]
    public async Task Revisions_come_with_their_authors_each_read_once()
    {
        _budgets.ListRevisionsAsync("26-0003", Arg.Any<CancellationToken>()).Returns(
            [Stored() with { Revision = 1 }, Stored(), Stored() with { MadeById = null }]);

        var revisions = await Service.ListRevisionsAsync("26-0003", Cancellation);

        revisions.Select(r => r.MadeBy).Should().Equal(Author, Author, null);
        await _budgets.Received(1).FindAuthorAsync(UserId, Arg.Any<CancellationToken>());
    }

    private static Budget Stored() => new(BudgetId, 0, true, true, 32.5, 45, 60, false, ClientId, "Máquina de corte", "Eng.ª Maria", UserId,
        "26-0003", "Transferência", 12345.67, "Texto", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);

    private static Budget FromNew(NewBudget b) => new("6a0000000000000000000001", b.Revision, false, false, b.Margin, b.DaysNeededToDoTheWork,
        b.Validity, false, b.ClientId, b.Descriptive, b.PersonInCharge, b.MadeById, b.BudgetNumber, b.PaymentMethod, b.Total, b.Description, b.Date, 0);

    private static JsonElement Body(string madeById, string clientId, string descriptive = "Máquina de corte", string person = "Eng.ª Maria") =>
        JsonSerializer.SerializeToElement(new { clientId, madeById, descriptive, personInCharge = person });

    private static JsonElement Edit(string madeById, string descriptive) =>
        JsonSerializer.SerializeToElement(new { madeById, descriptive, personInCharge = "Eng.ª Maria" });
}

internal static class JsonText
{
    public static JsonElement AsJson(this string json) => JsonElement.Parse(json);
}
