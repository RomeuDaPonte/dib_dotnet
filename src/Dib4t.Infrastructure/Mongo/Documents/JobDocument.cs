using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary>
/// <c>jobs</c> (legacy <c>models/job.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>. The fields
/// after <see cref="Version"/> are the ones the legacy app adds later with <c>$set</c> (state changes, statistics).
/// </summary>
public sealed class JobDocument
{
    public ObjectId Id { get; set; }

    public double InvoicedPercent { get; set; }

    public double FinalPrice { get; set; } = 1;

    /// <summary>One of <see cref="JobStates"/>.</summary>
    public string State { get; set; } = JobStates.Development;

    public string OrderIdentification { get; set; } = "Por preencher";

    public bool IsOfficialOrderIdentification { get; set; }

    public double Components { get; set; }

    public double Automation { get; set; }

    public double Materials { get; set; }

    public double Others { get; set; }

    /// <summary>Default 0; one production job has <c>null</c> (§4.5 item 2), so nullable.</summary>
    public double? CostNonQuality { get; set; } = 0;

    /// <summary><see cref="CommentTypes"/>; missing in most old jobs (read as empty).</summary>
    public List<string?> CommentType { get; set; } = [];

    /// <summary>→ <c>budgets._id</c>.</summary>
    public ObjectId? Budget { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>Mongoose default <c>Date.now</c>: set by the create use case from <c>TimeProvider</c>.</summary>
    public DateTime? StartDate { get; set; }

    public JobDevStatisticDocument? DevStatistic { get; set; }

    public JobMachiningStatisticDocument? MachiningStatistic { get; set; }

    public JobAssemblyStatisticDocument? AssemblyStatistic { get; set; }

    /// <summary>One of <see cref="JobPriorities"/>, or missing: the legacy default resolves to <c>undefined</c> (§4.4.3).</summary>
    public string? Priority { get; set; }

    public int Version { get; set; }

    public string? Comments { get; set; }

    public JobStatisticsDatesDocument? JobStatisticsDates { get; set; }

    /// <summary>Stored <c>null</c> in many imported jobs; years before 1970 occur (typo year 0022, §4.5 item 5).</summary>
    public DateTime? DeliveryDate { get; set; }

    public string? InvoicedBy { get; set; }

    /// <summary>E.g. <c>isHightPriority</c> of old jobs (§4.5 item 4).</summary>
    public BsonDocument? ExtraElements { get; set; }
}

/// <summary>
/// <c>jobs.devStatistic</c> (also used for "Em Aprovação" and "Aprovado"). Embedded documents get a new <c>_id</c>
/// when created (§4.3 rule 5); the legacy app writes it after the defaulted fields.
/// </summary>
public sealed class JobDevStatisticDocument
{
    public double NumberOfHoursUsed { get; set; }

    public double BudgetedHours { get; set; }

    public double Deviation { get; set; }

    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public BsonDocument? ExtraElements { get; set; }
}

/// <summary><c>jobs.machiningStatistic</c>. See <see cref="JobDevStatisticDocument"/>.</summary>
public sealed class JobMachiningStatisticDocument
{
    public double NumberOfHoursUsed { get; set; }

    public double BudgetedHours { get; set; }

    public double Deviation { get; set; }

    public string VerificationAndShapeControl { get; set; } = QuestionResponses.NotApplicable;

    public string DimensionalVerificationAndControl { get; set; } = QuestionResponses.NotApplicable;

    public string AnalysisOfTheDegreeOfFinish { get; set; } = QuestionResponses.NotApplicable;

    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string? ValidatedBy { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}

/// <summary><c>jobs.assemblyStatistic</c>. See <see cref="JobDevStatisticDocument"/>.</summary>
public sealed class JobAssemblyStatisticDocument
{
    public double NumberOfHoursUsed { get; set; }

    public double BudgetedHours { get; set; }

    public double Deviation { get; set; }

    public string PneumaticCircuits { get; set; } = QuestionResponses.NotApplicable;

    public string ElectricalBoardsAndCircuits { get; set; } = QuestionResponses.NotApplicable;

    public string Kinematics { get; set; } = QuestionResponses.NotApplicable;

    /// <summary>Element <c>programmingFeaturesPLC</c>.</summary>
    public string ProgrammingFeaturesPlc { get; set; } = QuestionResponses.NotApplicable;

    /// <summary>Element <c>operator_equipmentSafetyConditions</c>.</summary>
    public string OperatorEquipmentSafetyConditions { get; set; } = QuestionResponses.NotApplicable;

    public string VisualInspection { get; set; } = QuestionResponses.NotApplicable;

    /// <summary><c>"1"</c>, <c>"2"</c> or <c>"3"</c>: a <b>string</b>.</summary>
    public string ValidTryOnFirstTest { get; set; } = QuestionResponses.DefaultFirstTestTry;

    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string? ValidatedBy { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}

/// <summary><c>jobs.jobStatisticsDates</c>: when each state ended. <c>_id</c> first (created empty, filled later).</summary>
public sealed class JobStatisticsDatesDocument
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public DateTime? DevEndedInDate { get; set; }

    public DateTime? OnApprovalEndedInDate { get; set; }

    public DateTime? ApprovedEndedInDate { get; set; }

    public DateTime? MachiningEndedInDate { get; set; }

    public DateTime? AssemblyEndedInDate { get; set; }

    public DateTime? ForInvoicingEndedInDate { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
