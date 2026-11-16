namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// The cleanup job's calendar (PLAN.md §3.3), in a configured time zone while the container runs in UTC: the next
/// local <c>02:00</c>, and the retention cutoff in calendar months. Wall-clock times are resolved to an instant the
/// way people read them: a time skipped by a DST change runs at the first valid minute after the gap, a time that
/// happens twice runs at its first occurrence (in Lisbon 02:00 happens exactly once every day: the clock changes at 01:00).
/// </summary>
internal static class LogCleanupSchedule
{
    /// <summary>The first instant after <paramref name="now"/> whose local time in <paramref name="zone"/> is <paramref name="at"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo zone, TimeOnly at)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var day = today; ; day = day.AddDays(1))
        {
            var run = ToInstant(day.ToDateTime(at), zone);
            if (run > now)
            {
                return run;
            }
        }
    }

    /// <summary>
    /// Entries older than this are deleted: <paramref name="now"/>'s local date and time minus
    /// <paramref name="months"/> calendar months (<see cref="DateTime.AddMonths"/>: 2027-03-31 02:00 → 2027-02-28 02:00).
    /// </summary>
    public static DateTimeOffset Cutoff(DateTimeOffset now, TimeZoneInfo zone, int months)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return ToInstant(TimeZoneInfo.ConvertTime(now, zone).DateTime.AddMonths(-months), zone);
    }

    /// <summary>A wall-clock time in <paramref name="zone"/> as an instant (see the class remarks for DST changes).</summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
