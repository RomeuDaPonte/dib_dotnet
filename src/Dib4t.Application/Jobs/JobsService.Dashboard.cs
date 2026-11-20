using Dib4t.Application.Common;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Jobs;

/// <summary>The dashboard's route (legacy <c>GET getValidTryOnFirstTestCountersByYear/:year</c>, PLAN.md §6, §7.3).</summary>
public sealed partial class JobsService
{
    /// <summary>
    /// <c>GET getValidTryOnFirstTestCountersByYear/:year</c>: the jobs in "Para faturação" or "Faturada" started in the
    /// year, by <c>assemblyStatistic.validTryOnFirstTest</c> "1", "2", "3" (checked against
    /// <c>tests/fixtures/legacy/jobs/job-dashboard-golden.json</c>). The year is the whole calendar year in the
    /// company's zone (D10 fixed, <see cref="DashboardYear"/>). A year legacy reads as an Invalid Date is its
    /// CastError: the 500 (D16), nothing queried.
    /// </summary>
    public async Task<Result<ValidTryCounters>> CountValidTryOnFirstTestAsync(string year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(year);
        if (DashboardYear.Range(year, options.Value.FindTimeZone() ?? TimeZoneInfo.Utc) is not { } range)
        {
            const string Reason = "CastError: Cast to date failed for value \"Invalid Date\" (type Date) at path \"startDate\" for model \"Job\"";
            LogCountersRefused(year, Reason);
            return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = Reason };
        }

        return await jobs.CountValidTryOnFirstTestAsync(range.From, range.To, cancellationToken);
    }

    [LoggerMessage(EventId = 173, Level = LogLevel.Warning, Message = "Dashboard counters of the year {Year} not counted (legacy 500): {Reason}")]
    private partial void LogCountersRefused(string year, string reason);
}
