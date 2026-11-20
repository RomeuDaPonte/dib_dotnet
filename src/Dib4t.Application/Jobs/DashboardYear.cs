using Dib4t.Application.Common.Validation;

namespace Dib4t.Application.Jobs;

/// <summary>
/// The year of <c>GET getValidTryOnFirstTestCountersByYear/:year</c> (the dashboard, PLAN.md §7.3). Legacy counts the
/// jobs with <c>startDate</c> in <c>[new Date(`${year}-01-01`), new Date(`${year}-12-31`)]</c>: UTC midnights, so
/// 31 December after 00:00 is in no year (D10). <b>D10 fixed</b> (ADR-0005): the whole calendar year in the company's
/// zone (<c>App:TimeZone</c>, Europe/Lisbon), <c>[1 January 00:00, 1 January of the next year 00:00)</c>, of the year
/// <c>new Date(`${year}-01-01`)</c> gives in UTC. Checked against <c>tests/fixtures/legacy/jobs/job-dashboard-golden.json</c>.
/// </summary>
/// <remarks>
/// A text either legacy date of which is invalid (<c>26</c>, <c>2026x</c>, <c>+275760</c>) is <c>null</c>: legacy's
/// Mongoose CastError, a 500. The dates are read as <see cref="JsDate.Parse"/> reads them (the ECMAScript format):
/// texts only V8's fallback parser accepts (<c>abc</c>, <c>0</c>, <c> 2026</c>, <c>10000</c>, <c>-000000</c>; legacy
/// counts some other year for them) are invalid here too, a known difference (the legacy and the new page send
/// the four-digit current year). A year outside 1–9999 takes the zone's offset on 1 January of the nearest year .NET
/// holds; before 1912 (Lisbon mean time, −0:36:45) .NET's zone data gives the offset in whole minutes (−0:36).
/// </remarks>
public static class DashboardYear
{
    /// <summary>The year's <c>[From, To)</c> as JavaScript time values (milliseconds since 1970 UTC), or <c>null</c> for legacy's CastError.</summary>
    public static (long From, long To)? Range(string year, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(zone);
        if (JsDate.Parse(year + "-01-01") is not { } start || JsDate.Parse(year + "-12-31") is null)
        {
            return null;
        }

        var utcYear = JsDate.UtcYear(start);
        return (NewYear(utcYear, zone), NewYear(utcYear + 1, zone));
    }

    /// <summary>1 January 00:00 of <paramref name="year"/> in <paramref name="zone"/>, as a time value.</summary>
    private static long NewYear(long year, TimeZoneInfo zone)
    {
        var near = (int)Math.Clamp(year, 1, 9999);
        var offset = zone.GetUtcOffset(new DateTime(near, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        return JsDate.StartOfUtcYear(year) - (long)offset.TotalMilliseconds;
    }
}
