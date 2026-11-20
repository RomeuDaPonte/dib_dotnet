using System.Globalization;
using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Entities;
using Dib4t.Application.Jobs;
using Dib4t.Application.Nas;
using Dib4t.Application.Printing;
using Dib4t.Application.Settings;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Jobs;

/// <summary>
/// <c>GET getValidTryOnFirstTestCountersByYear/:year</c> from the legacy code's own answers (<c>job-dashboard-golden.json</c>):
/// the year's range against the filter the legacy code with D10 fixed sends, the Invalid Dates, and the service.
/// (Counts are checked over HTTP on mongo:9.0.8 in <c>Dib4t.Server.Tests/Jobs/JobDashboardGoldenTests</c>.)
/// </summary>
public sealed class JobDashboardRulesTests
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.JobDashboardGolden));

    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    /// <summary>Texts only V8's fallback date parser reads (not ported): invalid in the new app.</summary>
    private static readonly string[] FallbackYears = ["abc", "0", " 2026", "10000", "-000000"];

    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly RecordingLogger<JobsService> _logger = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private JobsService Service => new(Substitute.For<IBudgetRepository>(), Substitute.For<IBudgetTaskRepository>(), Substitute.For<IEntityRepository>(), _jobs,
        Substitute.For<IPrinterSettingsRepository>(), Substitute.For<INasFileShare>(), Substitute.For<ILabelPrinter>(),
        new FakeTimeProvider(), Options.Create(new AppOptions()), _logger);

    /// <summary>The golden cases with a 4-digit or 6-digit year from 1912 (before it, .NET's Lisbon mean time is in whole minutes).</summary>
    public static TheoryData<string> ModernYears
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var item in Golden.GetProperty("cases").EnumerateArray())
            {
                var year = item.GetProperty("year").GetString()!;
                if (item.GetProperty("fixed").TryGetProperty("startDate", out _) && !FallbackYears.Contains(year)
                    && DashboardYear.Range(year, Lisbon) is { } range && JsDate.UtcYear(range.To) >= 1913)
                {
                    data.Add(year);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> InvalidYears
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var item in Golden.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("fixed").TryGetProperty("error", out _)))
            {
                data.Add(item.GetProperty("year").GetString()!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ModernYears))]
    public void The_year_is_the_range_the_legacy_code_with_D10_fixed_sends(string year)
    {
        var filter = Golden.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("year").GetString() == year).GetProperty("fixed").GetProperty("startDate");

        var range = DashboardYear.Range(year, Lisbon)!.Value;

        (JsDate.ToIsoString(range.From), JsDate.ToIsoString(range.To)).Should()
            .Be((filter.GetProperty("$gte").GetProperty("$date").GetString()!, filter.GetProperty("$lt").GetProperty("$date").GetString()!));
    }

    [Fact]
    public void The_modern_years_cover_the_D10_cases() =>
        ModernYears.Select(r => r.Data).Should().Contain(["2026", "2025", "1995", "1996", "9999", "+002026", "+010000"]);

    [Theory]
    [MemberData(nameof(InvalidYears))]
    public void A_year_legacy_reads_as_an_Invalid_Date_has_no_range(string year) => DashboardYear.Range(year, Lisbon).Should().BeNull();

    [Fact]
    public void Texts_only_the_V8_fallback_parser_reads_have_no_range()
    {
        foreach (var year in FallbackYears)
        {
            DashboardYear.Range(year, Lisbon).Should().BeNull(year);
        }
    }

    [Fact]
    public void Before_1912_the_offset_is_Lisbon_mean_time_in_whole_minutes()
    {
        var range = DashboardYear.Range("0999", Lisbon)!.Value;

        JsDate.ToIsoString(range.From).Should().Be("0999-01-01T00:36:00.000Z", "the golden file's Node (ICU) says 00:36:45; no job starts then");
    }

    [Fact]
    public void Lisbon_on_UTC_plus_1_moves_both_ends()
    {
        var range = DashboardYear.Range("1995", Lisbon)!.Value;

        (JsDate.ToIsoString(range.From), JsDate.ToIsoString(range.To)).Should().Be(("1994-12-31T23:00:00.000Z", "1995-12-31T23:00:00.000Z"));
    }

    [Fact]
    public void The_configured_zone_is_used() =>
        JsDate.ToIsoString(DashboardYear.Range("2026", TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"))!.Value.From)
            .Should().Be("2026-01-01T03:00:00.000Z");

    [Theory]
    [InlineData(0L, 1970L)]
    [InlineData(-1L, 1969L)]
    [InlineData(1_798_761_599_999L, 2026L)]
    [InlineData(1_798_761_600_000L, 2027L)]
    [InlineData(-62_198_755_200_000L, -1L)]
    [InlineData(8_640_000_000_000_000L, 275_760L)]
    public void UtcYear_is_getUTCFullYear(long time, long year) => JsDate.UtcYear(time).Should().Be(year);

    [Theory]
    [InlineData(1970L, 0L)]
    [InlineData(2026L, 1_767_225_600_000L)]
    [InlineData(0L, -62_167_219_200_000L)]
    [InlineData(10_000L, 253_402_300_800_000L)]
    public void StartOfUtcYear_is_Date_UTC(long year, long time) => JsDate.StartOfUtcYear(year).Should().Be(time);

    [Fact]
    public async Task The_service_counts_the_whole_year_in_the_company_zone()
    {
        _jobs.CountValidTryOnFirstTestAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new ValidTryCounters(4, 2, 1));

        var counted = await Service.CountValidTryOnFirstTestAsync("2026", Cancellation);

        counted.Value.Should().Be(new ValidTryCounters(4, 2, 1));
        await _jobs.Received(1).CountValidTryOnFirstTestAsync(Time("2026-01-01T00:00:00Z"), Time("2027-01-01T00:00:00Z"), Arg.Any<CancellationToken>());
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_year_is_the_legacy_500_with_the_cast_error_logged_and_nothing_counted()
    {
        var counted = await Service.CountValidTryOnFirstTestAsync("26", Cancellation);

        (counted.Error!.Kind, counted.Error.Message).Should().Be((ErrorKind.Failure, LegacyMessages.SomethingFailed));
        counted.Error!.Reason.Should().Be(Golden.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("year").GetString() == "26")
            .GetProperty("legacy").GetProperty("error").GetString());
        await _jobs.DidNotReceiveWithAnyArgs().CountValidTryOnFirstTestAsync(default, default, Cancellation);
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, int EventId, string Message, Exception? Exception)>(
            e => e.Level == LogLevel.Warning && e.EventId == 173 && e.Message.Contains("year 26", StringComparison.Ordinal));
    }

    private static long Time(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
}
