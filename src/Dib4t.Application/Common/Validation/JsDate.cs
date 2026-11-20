using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// JavaScript dates as the legacy code makes them from request values (checked against the legacy code:
/// <c>tests/fixtures/legacy/jobs/job-golden.json</c>): a time value is milliseconds since 1970-01-01 UTC, valid within
/// ±8.64e15 (ECMAScript TimeClip). <c>Joi.date()</c> (<see cref="JoiDate"/>) and Mongoose's Date cast
/// (<see cref="MongooseCasts.Date"/>) both end in <c>new Date(value)</c>.
/// </summary>
/// <remarks>
/// <c>new Date(text)</c> is implemented for the ECMAScript date-time string format as V8 reads it
/// (<c>YYYY</c>, <c>YYYY-MM</c>, <c>YYYY-MM-DD</c>, <c>±YYYYYY…</c>, each optionally followed by <c>THH:mm</c>,
/// <c>:ss</c>, <c>.fraction</c> and <c>Z</c>/<c>±HH:mm</c>; a date alone is UTC; a time without offset is local time,
/// which is UTC in the legacy production container; a day up to 31 rolls over into the next month, as
/// <c>MakeDay</c> does: <c>2030-02-30</c> is 2 March). V8's fallback parser for other texts (<c>Jan 15 2030</c>,
/// <c>2030/01/15</c>, surrounding spaces) is <b>not</b> ported: such a text is an invalid date here (a known
/// difference: the legacy dialog sends <c>yyyy-MM-dd</c>, and there are no other API clients, ADR-0004).
/// </remarks>
public static partial class JsDate
{
    /// <summary>The largest time value (ECMAScript: 100,000,000 days).</summary>
    public const double MaxTime = 8.64e15;

    private const long MillisecondsPerDay = 86_400_000;

    /// <summary>ECMAScript TimeClip: <c>null</c> (an invalid date) beyond ±8.64e15 or for NaN/±Infinity, else truncated to an integer.</summary>
    public static long? TimeClip(double time) =>
        double.IsFinite(time) && Math.Abs(time) <= MaxTime ? (long)Math.Truncate(time) : null;

    /// <summary><c>new Date(text).getTime()</c> for the ECMAScript date-time string format; <c>null</c> when invalid.</summary>
    public static long? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = IsoFormat().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var yearText = match.Groups["year"].Value;
        if (yearText == "-000000")
        {
            return null; // ECMAScript: minus zero is not a valid extended year
        }

        var year = long.Parse(yearText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var month = Number(match.Groups["month"], 1);
        var day = Number(match.Groups["day"], 1);
        var hour = Number(match.Groups["hour"], 0);
        var minute = Number(match.Groups["minute"], 0);
        var second = Number(match.Groups["second"], 0);
        var fraction = match.Groups["fraction"];
        var millisecond = fraction.Success ? Number(fraction.Value.PadRight(3, '0')[..3]) : 0;
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 24 || minute > 59 || second > 59
            || (hour == 24 && (minute != 0 || second != 0 || millisecond != 0)))
        {
            return null;
        }

        long offsetMinutes = 0;
        if (match.Groups["offset"] is { Success: true, Value: not "Z" } offset)
        {
            var hours = Number(offset.Value[1..3]);
            var minutes = Number(offset.Value[4..6]);
            if (hours > 23 || minutes > 59)
            {
                return null;
            }

            offsetMinutes = (offset.Value[0] == '-' ? -1 : 1) * ((hours * 60) + minutes);
        }

        var days = DaysFromCivil(year, month) + day - 1;
        var time = (days * (double)MillisecondsPerDay) + (((((hour * 60L) + minute - offsetMinutes) * 60) + second) * 1000) + millisecond;
        return TimeClip(time);
    }

    /// <summary>
    /// Joi 14.3.1 <c>date.toDate</c> (no format): a string of digits with an optional fraction is <c>parseFloat</c>ed
    /// and taken as milliseconds; any other string is <c>new Date(string)</c>; a finite number is milliseconds. The
    /// time value, or <c>null</c> when Joi refuses it ("must be a number of milliseconds or valid date string").
    /// </summary>
    public static long? JoiToDate(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) ? TimeClip(number) : null;
            case JsonValueKind.String:
                var text = value.GetString()!;
                return JoiDigits().IsMatch(text)
                    ? TimeClip(double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))
                    : Parse(text);
            default:
                return null;
        }
    }

    /// <summary>The ISO text JavaScript's <c>toISOString</c> writes (a year beyond 0–9999 as <c>±YYYYYY</c>).</summary>
    public static string ToIsoString(long time)
    {
        var days = Math.DivRem(time, MillisecondsPerDay, out var rest);
        if (rest < 0)
        {
            days--;
            rest += MillisecondsPerDay;
        }

        var (year, month, day) = CivilFromDays(days);
        var yearText = year is >= 0 and <= 9999
            ? year.ToString("0000", CultureInfo.InvariantCulture)
            : (year < 0 ? "-" : "+") + Math.Abs(year).ToString("000000", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{yearText}-{month:00}-{day:00}T{rest / 3_600_000:00}:{rest / 60_000 % 60:00}:{rest / 1000 % 60:00}.{rest % 1000:000}Z");
    }

    /// <summary>JavaScript <c>getUTCFullYear()</c> of a time value (any year a time value can hold).</summary>
    public static long UtcYear(long time)
    {
        var days = Math.DivRem(time, MillisecondsPerDay, out var rest);
        return CivilFromDays(rest < 0 ? days - 1 : days).Year;
    }

    /// <summary>JavaScript <c>Date.UTC(year, 0)</c>: 1 January 00:00 UTC of <paramref name="year"/> as a time value.</summary>
    public static long StartOfUtcYear(long year) => DaysFromCivil(year, 1) * MillisecondsPerDay;

    /// <summary>The time value as a UTC <see cref="DateTime"/>, or <c>null</c> outside the years 1–9999 .NET can hold.</summary>
    public static DateTime? ToDateTime(long time)
    {
        var epoch = DateTime.UnixEpoch;
        var min = (long)(DateTime.MinValue - epoch).TotalMilliseconds;
        var max = (long)(DateTime.MaxValue - epoch).TotalMilliseconds;
        return time < min || time > max ? null : epoch.AddMilliseconds(time);
    }

    private static int Number(Group group, int fallback) => group.Success ? Number(group.Value) : fallback;

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>Days from 1970-01-01 to the first of <paramref name="month"/> (proleptic Gregorian, H. Hinnant's algorithm).</summary>
    private static long DaysFromCivil(long year, int month)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - (era * 400);
        var dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146_097) + dayOfEra - 719_468;
    }

    private static (long Year, int Month, int Day) CivilFromDays(long days)
    {
        days += 719_468;
        var era = (days >= 0 ? days : days - 146_096) / 146_097;
        var dayOfEra = days - (era * 146_097);
        var yearOfEra = (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36_524) - (dayOfEra / 146_096)) / 365;
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        var mp = ((5 * dayOfYear) + 2) / 153;
        var day = (int)(dayOfYear - (((153 * mp) + 2) / 5) + 1);
        var month = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (yearOfEra + (era * 400) + (month <= 2 ? 1 : 0), month, day);
    }

    /// <summary>Joi's <c>/^[+-]?\d+(\.\d+)?$/</c> (JavaScript's <c>$</c> without the m flag is <c>\z</c>).</summary>
    [GeneratedRegex(@"^[+-]?[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JoiDigits();

    [GeneratedRegex(
        @"^(?<year>[0-9]{4}|[+-][0-9]{6})(?:-(?<month>[0-9]{2})(?:-(?<day>[0-9]{2}))?)?"
        + @"(?:T(?<hour>[0-9]{2}):(?<minute>[0-9]{2})(?::(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]+))?)?(?<offset>Z|[+-][0-9]{2}:[0-9]{2})?)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoFormat();
}

/// <summary>
/// <c>Joi.date()</c> (Joi 14.3.1, default options): presence, then <see cref="JsDate.JoiToDate"/>; an invalid date is
/// "must be a number of milliseconds or valid date string".
/// </summary>
internal sealed class JoiDate : JoiRule
{
    private bool _required;

    public static JoiDate Create() => new();

    public JoiDate Required()
    {
        _required = true;
        return this;
    }

    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent)
    {
        if (value is null)
        {
            return _required ? $"\"{label}\" is required" : null;
        }

        return JsDate.JoiToDate(value.Value) is null ? $"\"{label}\" must be a number of milliseconds or valid date string" : null;
    }
}

/// <summary>Mongoose 5.13's casts of the job fields (<c>lib/cast/date.js</c>, <c>lib/cast/boolean.js</c>).</summary>
internal static partial class MongooseCasts
{
    /// <summary>
    /// <c>castDate</c> of a value Joi accepted: a number is milliseconds; a string whose <c>Number()</c> is at least
    /// 275761 or below −271820 is milliseconds too; any other string is <c>new Date(string)</c> (so <c>"2030"</c> is
    /// the year 2030, while Joi read it as 2,030 ms). <c>null</c> = Mongoose refuses it (a cast error on save).
    /// </summary>
    public static long? Date(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) ? JsDate.TimeClip(number) : null;
            case JsonValueKind.String:
                var text = value.GetString()!;
                var asNumber = JsNumber.FromText(text);
                return !double.IsNaN(asNumber) && (asNumber >= 275761 || asNumber < -271820) ? JsDate.TimeClip(asNumber) : JsDate.Parse(text);
            default:
                return null;
        }
    }

    /// <summary>
    /// <c>castBoolean</c>: <c>true</c>, <c>"true"</c>, <c>1</c>, <c>"1"</c>, <c>"yes"</c> are true; <c>false</c>,
    /// <c>"false"</c>, <c>0</c>, <c>"0"</c>, <c>"no"</c> false (exact, case-sensitive); anything else is a cast error
    /// (<c>null</c>). Joi lets <c>"TRUE"</c> through, Mongoose does not.
    /// </summary>
    public static bool? Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null,
        },
        JsonValueKind.Number when value.TryGetDouble(out var number) && number is 1 => true,
        JsonValueKind.Number when value.TryGetDouble(out var number) && number is 0 => false,
        _ => null,
    };
}
