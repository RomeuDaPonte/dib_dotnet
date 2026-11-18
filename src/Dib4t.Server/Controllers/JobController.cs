using Dib4t.Application.Jobs;
using Dib4t.Contracts.Jobs;
using Dib4t.Domain.Jobs;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/job</c> (legacy <c>routes/job.js</c>, PLAN.md §6), every route T (any signed-in user; legacy checks no role
/// here). This step: create (with the production NAS move and labels, §5).
/// </summary>
[Route("api/job")]
[Authorize]
public sealed class JobController(JobsService jobs) : LegacyControllerBase
{
    /// <summary><c>POST /</c>: <c>createdBy</c> is the signed-in user's name (legacy <c>req.user.name</c>).</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await jobs.CreateAsync(body, User.FindFirst(Dib4tClaims.Name)?.Value, cancellationToken), ToResponse);
    }

    private static JobResponse ToResponse(JobCreated created)
    {
        var j = created.Job;
        return new JobResponse(j.InvoicedPercent, j.FinalPrice, j.State, j.OrderIdentification, j.IsOfficialOrderIdentification, j.Components,
            j.Automation, j.Materials, j.Others, j.CostNonQuality, j.CommentType, j.Id, BudgetResponses.ToResponse(created.Budget), j.CreatedBy,
            j.DueDate, j.StartDate, j.DevStatistic is { } dev ? ToResponse(dev) : null,
            j.MachiningStatistic is { } m
                ? new JobMachiningStatisticResponse(m.Hours.NumberOfHoursUsed, m.Hours.BudgetedHours, m.Hours.Deviation, m.VerificationAndShapeControl,
                    m.DimensionalVerificationAndControl, m.AnalysisOfTheDegreeOfFinish, m.Hours.Id, m.ValidatedBy)
                : null,
            j.AssemblyStatistic is { } a
                ? new JobAssemblyStatisticResponse(a.Hours.NumberOfHoursUsed, a.Hours.BudgetedHours, a.Hours.Deviation, a.PneumaticCircuits,
                    a.ElectricalBoardsAndCircuits, a.Kinematics, a.ProgrammingFeaturesPlc, a.OperatorEquipmentSafetyConditions, a.VisualInspection,
                    a.ValidTryOnFirstTest, a.Hours.Id, a.ValidatedBy)
                : null,
            j.Priority, j.Version);
    }

    private static JobHoursResponse ToResponse(JobHours hours) => new(hours.NumberOfHoursUsed, hours.BudgetedHours, hours.Deviation, hours.Id);
}
