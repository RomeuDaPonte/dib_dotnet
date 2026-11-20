using Dib4t.Application.Jobs;
using Dib4t.Contracts.Jobs;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/job</c> (legacy <c>routes/job.js</c>, PLAN.md §6), every route T (any signed-in user; legacy checks no role
/// here). Create (with the production NAS move and labels, §5), the jobs board's routes (the statistics transition,
/// due date, state (D1 fixed), priority, the reads) and the details page's (state dates (D17 fixed), registered values
/// (D18 fixed), single property, verifications) and the labels of the print dialog (Production only, §5). Paths are case-insensitive, as in Express.
/// </summary>
[Route("api/job")]
[Authorize]
public sealed class JobController(JobsService jobs, IHostEnvironment environment) : LegacyControllerBase
{
    private string? UserName => User.FindFirst(Dib4tClaims.Name)?.Value;

    /// <summary><c>POST /</c>: <c>createdBy</c> is the signed-in user's name (legacy <c>req.user.name</c>).</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.CreateAsync(body, UserName, cancellationToken),
            created => JobResponses.ToResponse(created.Job, BudgetResponses.ToResponse(created.Budget)));
    }

    /// <summary>
    /// <c>POST print/:budgetId</c>: <c>"ok"</c> as text (legacy <c>res.send("ok")</c>). Outside the Production environment
    /// it is the legacy 400 "Só funciona em produção!" (legacy <c>NODE_ENV !== "prod"</c>).
    /// </summary>
    [HttpPost("print/{budgetId}")]
    public async Task<IActionResult> Print(string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        if (error is not null)
        {
            return error;
        }

        var printed = await jobs.PrintLabelsAsync(budgetId, body, environment.IsProduction(), UserName, cancellationToken);
        return printed.IsSuccess ? LegacyResults.Text(StatusCodes.Status200OK, printed.Value) : printed.Error.ToLegacyResult();
    }

    /// <summary>
    /// <c>PUT statistics/:jobId</c>: 200 with the saved job; "Faturada" below 100 % billed is 405
    /// <c>{ reason, updatedJob }</c> (the other values are saved, as legacy).
    /// </summary>
    [HttpPut("statistics/{jobId}")]
    public async Task<IActionResult> SaveStatistics(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        if (error is not null)
        {
            return error;
        }

        var saved = await jobs.SaveStatisticsAsync(jobId, body, UserName, cancellationToken);
        if (saved.IsSuccess && !saved.Value.StateChanged)
        {
            return StatusCode(StatusCodes.Status405MethodNotAllowed,
                new JobStatisticsRefusedResponse(JobMessages.InvoicedBelow100, JobResponses.Detailed(saved.Value.Job)));
        }

        return Answer(saved, s => JobResponses.Detailed(s.Job));
    }

    [HttpPatch("dueDate/{jobId}")]
    public async Task<IActionResult> ChangeDueDate(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.ChangeDueDateAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>PATCH state/:jobId</c> (D1 fixed: the job of the route, not always "Obra não existe").</summary>
    [HttpPatch("state/{jobId}")]
    public async Task<IActionResult> ChangeState(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.ChangeStateAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    [HttpPatch("changeJobPriority/{jobId}")]
    public async Task<IActionResult> ChangePriority(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.ChangePriorityAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>PATCH statisticsDates/:jobId</c> (the details page's state dates; D17 fixed).</summary>
    [HttpPatch("statisticsDates/{jobId}")]
    public async Task<IActionResult> ChangeStatisticsDate(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.ChangeStatisticsDateAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>PATCH register/:jobId</c> (the cost table's registered values; D18 fixed).</summary>
    [HttpPatch("register/{jobId}")]
    public async Task<IActionResult> RegisterValue(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.RegisterValueAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>PATCH singleProperty/:jobId</c> (the details header and observations).</summary>
    [HttpPatch("singleProperty/{jobId}")]
    public async Task<IActionResult> ChangeSingleProperty(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.ChangeSinglePropertyAsync(jobId, body, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>PATCH updateVerifications/:jobId</c>: <c>validatedBy</c> is the signed-in user's name.</summary>
    [HttpPatch("updateVerifications/{jobId}")]
    public async Task<IActionResult> UpdateVerifications(string jobId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.UpdateVerificationsAsync(jobId, body, UserName, cancellationToken), JobResponses.Detailed);
    }

    /// <summary><c>GET getById/:jobId</c>: the job, or an empty 200 (legacy <c>res.send(null)</c>).</summary>
    [HttpGet("getById/{jobId}")]
    public async Task<IActionResult> FindById(string jobId, CancellationToken cancellationToken)
    {
        var found = await jobs.FindAsync(jobId, cancellationToken);
        return found.IsSuccess && found.Value is null ? new EmptyResult() : Answer(found, j => JobResponses.Detailed(j!));
    }

    [HttpGet("getCounts")]
    public async Task<IActionResult> Counts(CancellationToken cancellationToken) => Ok(JobResponses.ToResponse(await jobs.CountAsync(cancellationToken)));

    [HttpGet("getUnfinished")]
    public async Task<IActionResult> Unfinished(CancellationToken cancellationToken) =>
        Ok((await jobs.ListUnfinishedAsync(cancellationToken)).Select(JobResponses.Listed));

    [HttpGet("getAll")]
    public async Task<IActionResult> All(CancellationToken cancellationToken) =>
        Ok((await jobs.ListAllAsync(cancellationToken)).Select(JobResponses.Listed));

    [HttpGet("getByState/{state}")]
    public async Task<IActionResult> ByState(string state, CancellationToken cancellationToken) =>
        Answer(await jobs.ListByStateAsync(state, cancellationToken), list => list.Select(JobResponses.Listed));

    /// <summary><c>GET getByBudgetNumber/:budgetNumber</c>: the job, or an empty 200.</summary>
    [HttpGet("getByBudgetNumber/{budgetNumber}")]
    public async Task<IActionResult> ByBudgetNumber(string budgetNumber, CancellationToken cancellationToken) =>
        await jobs.FindByBudgetNumberAsync(budgetNumber, cancellationToken) is { } job ? Ok(JobResponses.WithBudgetRefs(job)) : new EmptyResult();

    /// <summary><c>GET getByBudgetId/:budgetId</c>: the job's id as a JSON string (legacy <c>res.send(job._id)</c>).</summary>
    [HttpGet("getByBudgetId/{budgetId}")]
    public async Task<IActionResult> IdByBudgetId(string budgetId, CancellationToken cancellationToken)
    {
        var found = await jobs.FindIdByBudgetIdAsync(budgetId, cancellationToken);
        return found.IsSuccess ? new JsonResult(found.Value) : Answer(found, id => id);
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> Get(string jobId, CancellationToken cancellationToken) =>
        Answer(await jobs.GetAsync(jobId, cancellationToken), JobResponses.Detailed);
}
