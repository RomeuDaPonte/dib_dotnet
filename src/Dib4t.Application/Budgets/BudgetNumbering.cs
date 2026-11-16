using Dib4t.Domain.Budgets;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Budgets;

/// <summary>
/// One budget number (or revision) at a time (PLAN.md §4.7): the legacy app counts and inserts without a lock, so two
/// users creating budgets at the same moment can get the same number. Here a process-wide lock covers the count and
/// the insert, and the collision guard skips a number that is already in use (e.g. after a budget was deleted from an
/// imported backup, the legacy count would hand out an existing number again). With no gap in the data the number is
/// exactly the legacy one.
/// </summary>
public sealed partial class BudgetNumbering(ILogger<BudgetNumbering> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Runs <paramref name="action"/> while no other numbering runs (new revisions use it too).</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The next free number of <paramref name="year"/>: <see cref="BudgetNumber.Next"/> of the legacy count, then the
    /// following ones while a number is taken. Call inside <see cref="RunExclusiveAsync{T}"/>.
    /// </summary>
    public async Task<string> NextAsync(IBudgetRepository budgets, int year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        var count = await budgets.CountNumbersSinceAsync(BudgetNumber.YearStart(year), cancellationToken);
        var number = BudgetNumber.Next(year, count);
        for (var sequence = count + 1; await budgets.NumberExistsAsync(number, cancellationToken); number = BudgetNumber.Format(year, ++sequence))
        {
            LogNumberTaken(number);
        }

        return number;
    }

    [LoggerMessage(EventId = 79, Level = LogLevel.Warning, Message = "Budget number {BudgetNumber} is already in use; the next one is tried (legacy would repeat it)")]
    private partial void LogNumberTaken(string budgetNumber);
}
