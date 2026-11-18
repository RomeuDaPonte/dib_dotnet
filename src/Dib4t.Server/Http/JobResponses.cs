using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Jobs;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Jobs;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Jobs;

namespace Dib4t.Server.Http;

/// <summary>A job as the job routes answer it, with its budget as each route populates it (PLAN.md §6).</summary>
internal static class JobResponses
{
    private static readonly JsonSerializerOptions ExtraOptions = new() { Converters = { new JsDateJsonConverter(), new JsNumberJsonConverter() } };

    /// <summary>The job of <c>getById</c>, <c>/:jobId</c> and the saves: budget, client and author populated.</summary>
    public static JobResponse<BudgetResponse> Detailed(PopulatedJob read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return ToResponse(read.Job, read.Budget is { } budget ? BudgetResponses.ToResponse(budget) : null);
    }

    /// <summary>A job of the lists: budget and client populated, the author an id.</summary>
    public static JobResponse<JobListBudgetResponse> Listed(PopulatedJob read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return ToResponse(read.Job, read.Budget is { } details ? ToListBudget(details) : null);
    }

    /// <summary>The job of <c>getByBudgetNumber</c>: the budget only.</summary>
    public static JobResponse<BudgetRefsResponse> WithBudgetRefs(PopulatedJob read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return ToResponse(read.Job, read.Budget is { } details ? ToRefs(details.Budget) : null);
    }

    public static JobResponse<TBudget> ToResponse<TBudget>(Job j, TBudget? budget)
        where TBudget : class
    {
        ArgumentNullException.ThrowIfNull(j);
        return new JobResponse<TBudget>(j.InvoicedPercent, j.FinalPrice, j.State, j.OrderIdentification, j.IsOfficialOrderIdentification, j.Components,
            j.Automation, j.Materials, j.Others, j.CostNonQuality, j.CommentType, j.Id, budget, j.CreatedBy, j.DueDate, j.StartDate,
            j.DevStatistic is { } dev ? new JobHoursResponse(dev.NumberOfHoursUsed, dev.BudgetedHours, dev.Deviation, dev.Id) : null,
            j.MachiningStatistic is { } m
                ? new JobMachiningStatisticResponse(m.Hours.NumberOfHoursUsed, m.Hours.BudgetedHours, m.Hours.Deviation, m.VerificationAndShapeControl,
                    m.DimensionalVerificationAndControl, m.AnalysisOfTheDegreeOfFinish, m.Hours.Id, m.ValidatedBy)
                : null,
            j.AssemblyStatistic is { } a
                ? new JobAssemblyStatisticResponse(a.Hours.NumberOfHoursUsed, a.Hours.BudgetedHours, a.Hours.Deviation, a.PneumaticCircuits,
                    a.ElectricalBoardsAndCircuits, a.Kinematics, a.ProgrammingFeaturesPlc, a.OperatorEquipmentSafetyConditions, a.VisualInspection,
                    a.ValidTryOnFirstTest, a.Hours.Id, a.ValidatedBy)
                : null,
            j.Priority, j.Version)
        {
            Comments = j.Comments,
            JobStatisticsDates = j.StatisticsDates is { } d
                ? new JobStatisticsDatesResponse(d.Id, d.DevEndedInDate, d.OnApprovalEndedInDate, d.ApprovedEndedInDate, d.MachiningEndedInDate,
                    d.AssemblyEndedInDate, d.ForInvoicingEndedInDate)
                : null,
            DeliveryDate = j.DeliveryDate,
            InvoicedBy = j.InvoicedBy,
            Extra = j.Extra.Count == 0 ? null : j.Extra.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value, ExtraOptions), StringComparer.Ordinal),
        };
    }

    public static JobCountsResponse ToResponse(JobCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new(counts.All, counts.Development, counts.OnApproval, counts.Approved, counts.Machining, counts.Assembly, counts.ForInvoicing);
    }

    private static JobListBudgetResponse ToListBudget(BudgetDetails details)
    {
        var b = details.Budget;
        return new(b.Revision, b.Complete, b.WasPdfDownloaded, b.Margin, b.DaysNeededToDoTheWork, b.Validity, b.HasGenerateJob, b.Id,
            details.Client is { } client ? BudgetResponses.ToResponse(client) : null, b.Descriptive, b.PersonInCharge, b.MadeById, b.BudgetNumber,
            b.PaymentMethod, b.Total, b.Description, b.Date, b.Version);
    }

    private static BudgetRefsResponse ToRefs(Budget b) =>
        new(b.Revision, b.Complete, b.WasPdfDownloaded, b.Margin, b.DaysNeededToDoTheWork, b.Validity, b.HasGenerateJob, b.Id, b.ClientId,
            b.Descriptive, b.PersonInCharge, b.MadeById, b.BudgetNumber, b.PaymentMethod, b.Total, b.Description, b.Date, b.Version);
}
