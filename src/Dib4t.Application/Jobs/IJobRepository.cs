using Dib4t.Domain.Jobs;

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
}

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
