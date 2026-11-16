using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Entities;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Dib4t.Application.Tests.Budgets;

/// <summary>
/// <c>GET /api/budget/getPdf/:id</c>: the legacy order (populated budget → document → <c>wasPdfDownloaded</c> saved →
/// file), every legacy TypeError a 500 "Something failed." with nothing saved (D16), the Lisbon date (D23), the log.
/// </summary>
public sealed class BudgetPdfServiceTests
{
    private const string BudgetId = "5f1e9a2b3c4d5e6f70810001";
    private const string ClientId = "5f1e9a2b3c4d5e6f70820001";
    private const string UserId = "5f1e9a2b3c4d5e6f70830001";

    private static readonly byte[] Pdf = "%PDF-1.3 test"u8.ToArray();

    private readonly IBudgetRepository _budgets = Substitute.For<IBudgetRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IBudgetPdfGenerator _generator = Substitute.For<IBudgetPdfGenerator>();
    private readonly RecordingLogger<BudgetPdfService> _logger = new();
    private readonly List<string> _calls = [];

    public BudgetPdfServiceTests()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored());
        _budgets.FindAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns(new BudgetAuthor(UserId, "Ana Administradora", "ana@dib4t.local", [], "Desenvolvimento", 0));
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new BusinessEntity(ClientId, "Metalúrgica do Ave", "500100200", "Rua 1", "4760-100", "Famalicão", "30 dias", "Cliente", 0));
        _generator.Generate(Arg.Any<BudgetPdfContent>()).Returns(_ =>
        {
            _calls.Add("generate");
            return Pdf;
        });
        _budgets.SetPdfDownloadedAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("save");
            return Stored() with { WasPdfDownloaded = true };
        });
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_PDF_prints_the_populated_budget_and_then_the_flag_is_saved()
    {
        var result = await Service().GetAsync(BudgetId.ToUpperInvariant(), Cancellation);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new BudgetPdf(Pdf, "26-0007", 2));
        _calls.Should().Equal("generate", "save");
        _generator.Received(1).Generate(new BudgetPdfContent("26-0007", 2, new DateOnly(2026, 2, 1), "Ana Administradora", "ana@dib4t.local",
            "Metalúrgica do Ave", "Rua 1", "Famalicão", "4760-100", "500100200", "Eng.ª Maria", "Máquina de corte", "Texto", 45, 60, "30 dias", 43.5));
        _logger.Entries.Should().ContainSingle(e => e.EventId == 83 && e.Level == LogLevel.Information
            && e.Message == $"Budget {BudgetId} (26-0007 R2): PDF made, {Pdf.Length} bytes; wasPdfDownloaded set True");
    }

    [Theory]
    [InlineData("Europe/Lisbon", 25)]
    [InlineData("UTC", 24)]
    public async Task The_document_date_is_the_day_in_the_business_time_zone_D23(string zone, int day)
    {
        // Sample 09: 2022-05-24T23:00:37Z is 25 May in Lisbon (summer time); legacy printed the UTC day.
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { Date = new DateTime(2022, 5, 24, 23, 0, 37, DateTimeKind.Utc) });

        await Service(zone).GetAsync(BudgetId, Cancellation);

        _generator.Received(1).Generate(Arg.Is<BudgetPdfContent>(c => c.DocumentDate == new DateOnly(2022, 5, day)));
    }

    [Fact]
    public async Task A_budget_with_a_job_or_already_downloaded_still_gets_its_PDF_as_legacy()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { HasGenerateJob = true, WasPdfDownloaded = true });

        var result = await Service().GetAsync(BudgetId, Cancellation);

        result.IsSuccess.Should().BeTrue();
        await _budgets.Received(1).SetPdfDownloadedAsync(BudgetId, Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle(e => e.EventId == 83 && e.Message.EndsWith("wasPdfDownloaded set False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_texts_and_numbers_reach_the_generator_as_missing()
    {
        _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { Description = null, Total = null, PersonInCharge = null });
        _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new BusinessEntity(ClientId, "Só nome", null, null, null, null, null, null, 0));

        await Service().GetAsync(BudgetId, Cancellation);

        _generator.Received(1).Generate(Arg.Is<BudgetPdfContent>(c => c.Description == null && c.Total == null && c.PersonInCharge == null
            && c.ClientAddress == null && c.ClientFiscalNumber == null && c.ClientName == "Só nome"));
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "not-an-id", "not a budget id" },
        { "5f1e9a2b3c4d5e6f708100ff", "no budget with this id" },
        { "author", "author (madeBy) does not exist" },
        { "client", "client does not exist" },
        { "date", "has no date" },
        { "deleted", "deleted meanwhile" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task What_legacy_fails_on_is_a_500_and_nothing_is_saved(string failure, string reason)
    {
        var id = failure is "not-an-id" or "5f1e9a2b3c4d5e6f708100ff" ? failure : BudgetId;
        switch (failure)
        {
            case "author":
                _budgets.FindAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns((BudgetAuthor?)null);
                break;
            case "client":
                _entities.FindByIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns((BusinessEntity?)null);
                break;
            case "date":
                _budgets.FindByIdAsync(BudgetId, Arg.Any<CancellationToken>()).Returns(Stored() with { Date = null });
                break;
            case "deleted":
                _budgets.SetPdfDownloadedAsync(BudgetId, Arg.Any<CancellationToken>()).Returns((Budget?)null);
                break;
            default:
                break;
        }

        var result = await Service().GetAsync(id, Cancellation);

        result.IsSuccess.Should().BeFalse();
        var error = result.Error!;
        error.Kind.Should().Be(ErrorKind.Failure);
        error.Message.Should().Be(LegacyMessages.SomethingFailed);
        error.Reason.Should().Contain(reason);
        if (failure != "deleted")
        {
            await _budgets.DidNotReceive().SetPdfDownloadedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        _logger.Entries.Should().ContainSingle(e => e.EventId == 84 && e.Level == LogLevel.Warning && e.Message.Contains(reason, StringComparison.Ordinal));
    }

    private BudgetPdfService Service(string zone = "Europe/Lisbon") =>
        new(_budgets, _entities, _generator, Options.Create(new AppOptions { TimeZone = zone }), _logger);

    private static Budget Stored() => new(BudgetId, 2, true, false, 35, 45, 60, false, ClientId, "Máquina de corte", "Eng.ª Maria",
        UserId, "26-0007", "30 dias", 43.5, "Texto", new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), 0);
}
