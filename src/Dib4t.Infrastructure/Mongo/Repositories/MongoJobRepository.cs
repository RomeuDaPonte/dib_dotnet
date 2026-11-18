using Dib4t.Application.Jobs;
using Dib4t.Domain.Jobs;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary><c>jobs</c> (legacy <c>repositories/jobRepository.js</c> and the saves of <c>routes/job.js</c>).</summary>
internal sealed class MongoJobRepository(IMongoDatabase database) : IJobRepository
{
    private readonly IMongoCollection<JobDocument> _jobs = database.Collection<JobDocument>();

    public async Task<Job> InsertAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        // new Job({ budget, createdBy, ...req.body }) + setNewStatistics(tasks): JobDocument's member order is the
        // legacy one (defaults, then budget, createdBy, dueDate, startDate, the statistics, priority, __v); the costs
        // are 0, deviation = budgeted hours, each statistic gets a new _id after its fields.
        var document = new JobDocument
        {
            Id = ObjectId.GenerateNewId(),
            FinalPrice = job.FinalPrice,
            State = job.State,
            OrderIdentification = job.OrderIdentification,
            IsOfficialOrderIdentification = job.IsOfficialOrderIdentification,
            Budget = ObjectId.Parse(job.BudgetId),
            CreatedBy = job.CreatedBy,
            DueDate = job.DueDate,
            StartDate = job.StartDate,
            DevStatistic = new JobDevStatisticDocument { BudgetedHours = job.DevelopmentHours, Deviation = job.DevelopmentHours },
            MachiningStatistic = new JobMachiningStatisticDocument { BudgetedHours = job.MachiningHours, Deviation = job.MachiningHours },
            AssemblyStatistic = new JobAssemblyStatisticDocument { BudgetedHours = job.AssemblyHours, Deviation = job.AssemblyHours },
            Priority = JobPriorities.Medium,
            Version = 0,
        };
        await _jobs.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    internal static Job ToDomain(JobDocument d) => new(
        d.Id.ToString(),
        d.InvoicedPercent,
        d.FinalPrice,
        d.State,
        d.OrderIdentification,
        d.IsOfficialOrderIdentification,
        d.Components,
        d.Automation,
        d.Materials,
        d.Others,
        d.CostNonQuality,
        d.CommentType,
        d.Budget?.ToString(),
        d.CreatedBy,
        d.DueDate,
        d.StartDate,
        d.DevStatistic is { } dev ? new JobHours(dev.NumberOfHoursUsed, dev.BudgetedHours, dev.Deviation, dev.Id.ToString()) : null,
        d.MachiningStatistic is { } m
            ? new JobMachiningStatistic(new JobHours(m.NumberOfHoursUsed, m.BudgetedHours, m.Deviation, m.Id.ToString()),
                m.VerificationAndShapeControl, m.DimensionalVerificationAndControl, m.AnalysisOfTheDegreeOfFinish, m.ValidatedBy)
            : null,
        d.AssemblyStatistic is { } a
            ? new JobAssemblyStatistic(new JobHours(a.NumberOfHoursUsed, a.BudgetedHours, a.Deviation, a.Id.ToString()),
                a.PneumaticCircuits, a.ElectricalBoardsAndCircuits, a.Kinematics, a.ProgrammingFeaturesPlc, a.OperatorEquipmentSafetyConditions,
                a.VisualInspection, a.ValidTryOnFirstTest, a.ValidatedBy)
            : null,
        d.Priority,
        d.Version);
}
