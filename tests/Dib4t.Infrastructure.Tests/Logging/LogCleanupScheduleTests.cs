using System.Globalization;
using Dib4t.Infrastructure.Logging;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>The cleanup calendar in Europe/Lisbon (PLAN.md §3.3): 02:00 once every day, DST days, month ends.</summary>
public sealed class LogCleanupScheduleTests
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    private static readonly TimeOnly TwoAm = new(2, 0);

    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Theory]
    [InlineData("2027-01-15T01:59:59Z", "2027-01-15T02:00:00Z")] // winter: Lisbon = UTC
    [InlineData("2027-01-15T02:00:00Z", "2027-01-16T02:00:00Z")] // exactly at the run: the next day
    [InlineData("2027-07-15T00:30:00Z", "2027-07-15T01:00:00Z")] // summer: 02:00 WEST = 01:00 UTC
    [InlineData("2027-07-15T01:00:00Z", "2027-07-16T01:00:00Z")]
    [InlineData("2027-03-28T00:59:00Z", "2027-03-28T01:00:00Z")] // spring forward (01:00 → 02:00): 02:00 WEST exists once
    [InlineData("2027-03-27T02:00:00Z", "2027-03-28T01:00:00Z")] // the day before → 23 h later
    [InlineData("2027-10-31T00:30:00Z", "2027-10-31T02:00:00Z")] // fall back (02:00 → 01:00): 02:00 WET, after the repeated hour
    [InlineData("2027-10-30T01:00:00Z", "2027-10-31T02:00:00Z")] // the day before → 25 h later
    [InlineData("2027-12-31T23:00:00Z", "2028-01-01T02:00:00Z")] // year end
    public void Next_run_is_the_next_local_02_00(string now, string expected) =>
        LogCleanupSchedule.NextRun(Utc(now), Lisbon, TwoAm).Should().Be(Utc(expected));

    [Fact]
    public void There_is_exactly_one_run_every_day_of_a_year_with_both_DST_changes()
    {
        var runs = new List<DateTimeOffset>();
        var now = Utc("2027-01-01T00:00:00Z");
        while (now < Utc("2028-01-01T00:00:00Z"))
        {
            now = LogCleanupSchedule.NextRun(now, Lisbon, TwoAm);
            runs.Add(now);
        }

        runs.Should().HaveCount(366); // 2027-01-01 … 2028-01-01 inclusive
        runs.Select(r => TimeZoneInfo.ConvertTime(r, Lisbon)).Should().OnlyContain(local => local.Hour == 2 && local.Minute == 0);
        runs.Select(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r, Lisbon).DateTime)).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("2027-03-31T01:00:00Z", 1, "2027-02-28T02:00:00Z")] // 31 Mar 02:00 WEST → 28 Feb 02:00 WET
    [InlineData("2028-03-31T01:00:00Z", 1, "2028-02-29T02:00:00Z")] // leap year
    [InlineData("2027-05-31T01:00:00Z", 1, "2027-04-30T01:00:00Z")]
    [InlineData("2027-01-15T02:00:00Z", 1, "2026-12-15T02:00:00Z")]
    [InlineData("2027-11-15T02:00:00Z", 1, "2027-10-15T01:00:00Z")] // WET now, WEST a month ago
    [InlineData("2027-07-15T01:00:00Z", 3, "2027-04-15T01:00:00Z")]
    public void Cutoff_is_calendar_months_in_local_time(string now, int months, string expected) =>
        LogCleanupSchedule.Cutoff(Utc(now), Lisbon, months).Should().Be(Utc(expected));

    [Fact]
    public void Skipped_local_times_move_after_the_gap_and_repeated_ones_take_the_first_occurrence()
    {
        LogCleanupSchedule.ToInstant(new DateTime(2027, 3, 28, 1, 30, 0), Lisbon).Should().Be(Utc("2027-03-28T01:00:00Z"));
        LogCleanupSchedule.ToInstant(new DateTime(2027, 10, 31, 1, 30, 0), Lisbon).Should().Be(Utc("2027-10-31T00:30:00Z"));
        LogCleanupSchedule.NextRun(Utc("2027-03-27T12:00:00Z"), Lisbon, new TimeOnly(1, 30)).Should().Be(Utc("2027-03-28T01:00:00Z"));
    }
}
