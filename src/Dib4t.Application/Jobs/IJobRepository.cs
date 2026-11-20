using Dib4t.Application.Budgets;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Jobs;

/// <summary>The <c>jobs</c> collection (legacy <c>repositories/jobRepository.js</c> and the job routes' saves).</summary>
public interface IJobRepository
{
    /// <summary>
    /// Legacy <c>new Job({ budget, createdBy, ...req.body })</c> + <c>setNewStatistics(tasks)</c> + <c>save()</c>:
    /// the Mongoose defaults first (schema order: <c>invoicedPercent</c> … <c>commentType</c>), then <c>budget</c>,
    /// <c>createdBy</c>, <c>dueDate</c>, <c>startDate</c>, the three statistics (each with a new <c>_id</c> after its
    /// fields), <c>priority: "Média"</c>, <c>__v: 0</c> (PLAN.md §4.3). Returns the stored job.
    /// </summary>
    Task<Job> InsertAsync(NewJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>Job.findById(id).populate(…)</c>: the job (Mongoose defaults applied for missing fields) with its
    /// budget as <paramref name="populate"/> asks; <c>null</c> when there is no such job.
    /// </summary>
    Task<PopulatedJob?> FindByIdAsync(string id, JobPopulation populate, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>Job.find(filter).sort({ dueDate: 1 }).populate(budget → client)</c>: the jobs of <paramref name="filter"/>
    /// by due date (missing first; equal dates in the collection's natural order), at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<PopulatedJob>> ListAsync(JobFilter filter, int limit, CancellationToken cancellationToken);

    /// <summary><c>Job.find(filter).countDocuments()</c>.</summary>
    Task<int> CountAsync(JobFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>getValidTryOnFirstTestCountersByYear</c>'s three counts: the jobs in "Para faturação" or "Faturada"
    /// whose <c>startDate</c> is a date in <c>[fromTime, toTime)</c> (JavaScript time values) and whose
    /// <c>assemblyStatistic.validTryOnFirstTest</c> is the text "1", "2" or "3" (MongoDB equality: a number 1 is not
    /// "1"; a missing, <c>null</c> or text <c>startDate</c> is in no range).
    /// </summary>
    Task<ValidTryCounters> CountValidTryOnFirstTestAsync(long fromTime, long toTime, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>getByBudgetNumber</c>: the first job (natural order) whose budget has <paramref name="budgetNumber"/>,
    /// with its budget (not its client or author); <c>null</c> when none.
    /// </summary>
    Task<PopulatedJob?> FindByBudgetNumberAsync(string budgetNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>getByBudgetId</c> for the labels (<c>POST print/:budgetId</c>): the first job of the budget (natural
    /// order) with its <c>startDate</c> as stored (missing, <c>null</c> or a date); <c>null</c> when it has none.
    /// </summary>
    Task<JobForLabels?> FindForLabelsAsync(string budgetId, CancellationToken cancellationToken);

    /// <summary>Legacy <c>getByBudgetId</c>: the id of the first job (natural order) made from <paramref name="budgetId"/>.</summary>
    Task<string?> FindIdByBudgetIdAsync(string budgetId, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>job.save()</c> after <paramref name="assignments"/> on the job as <see cref="FindByIdAsync"/> read it:
    /// Mongoose's <c>$set</c>/<c>$unset</c> (a path is written only when its value changes; a replaced array adds
    /// <c>$inc: { __v: 1 }</c>), no command when nothing changes. Returns the job as the legacy document holds it after
    /// the save (an unset field is missing, not its default), with <paramref name="read"/>'s populated budget.
    /// </summary>
    Task<PopulatedJob> SaveAsync(PopulatedJob read, IReadOnlyList<JobAssignment> assignments, CancellationToken cancellationToken);
}

/// <summary>How much of the job's budget a read populates (legacy <c>populate</c> of each repository function).</summary>
public enum JobPopulation
{
    /// <summary><c>populate("budget")</c>: the budget, its client and author as ids.</summary>
    Budget,

    /// <summary><c>populate({ path: "budget", populate: client })</c>: the lists.</summary>
    BudgetAndClient,

    /// <summary><c>populate({ path: "budget", populate: [client, madeBy] })</c>: <c>getById</c>.</summary>
    BudgetClientAndAuthor,
}

/// <summary>The dashboard's counters: jobs whose assembly was valid on the first, second or third test.</summary>
public sealed record ValidTryCounters(int One, int Two, int Three);

/// <summary>Which jobs a list or count reads.</summary>
/// <param name="States">Only these states; <c>null</c> = every state.</param>
/// <param name="Excluded">Not these states (<c>$nin</c>).</param>
public sealed record JobFilter(IReadOnlyList<string>? States, IReadOnlyList<string>? Excluded)
{
    public static JobFilter All { get; } = new(null, null);

    public static JobFilter InState(string state) => new([state], null);

    public static JobFilter NotIn(params string[] states) => new(null, states);
}

/// <summary>
/// A job with its budget as legacy <c>populate</c> gives it: <see cref="Budget"/> is <c>null</c> when the referenced
/// budget is gone (or not populated); <see cref="Budget"/>.<c>Client</c>/<c>MadeBy</c> are <c>null</c> when not
/// populated or gone.
/// </summary>
public sealed record PopulatedJob(Job Job, BudgetDetails? Budget, JobPopulation Population);

/// <summary>A job to insert (the create dialog's values, the budget, the creator and the budgeted hours).</summary>
/// <param name="BudgetId">The revision the job is made from.</param>
/// <param name="FinalPrice">The agreed price.</param>
/// <param name="DueDate">"Data de entrega" (UTC).</param>
/// <param name="State">The initial state.</param>
/// <param name="OrderIdentification">The client's order reference.</param>
/// <param name="IsOfficialOrderIdentification">Whether the reference is the official one.</param>
/// <param name="CreatedBy">The signed-in user's name; <c>null</c> is not written (legacy <c>undefined</c>).</param>
/// <param name="StartDate">Now (UTC, milliseconds).</param>
/// <param name="DevelopmentHours">Budgeted development hours (also the deviation).</param>
/// <param name="MachiningHours">Budgeted machining hours.</param>
/// <param name="AssemblyHours">Budgeted assembly hours.</param>
public sealed record NewJob(
    string BudgetId,
    double FinalPrice,
    DateTime DueDate,
    string State,
    string OrderIdentification,
    bool IsOfficialOrderIdentification,
    string? CreatedBy,
    DateTime StartDate,
    double DevelopmentHours,
    double MachiningHours,
    double AssemblyHours);

/// <summary>What the labels need of a job: its id and <c>startDate</c> as stored (Mongoose fills a missing one with now).</summary>
public sealed record JobForLabels(string Id, Stored<DateTime> StartDate);
