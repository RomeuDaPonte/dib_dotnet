using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary>
/// <c>budgets</c> (legacy <c>models/budget.js</c>, PLAN.md §4.2). Persistence shape only: property order is the field
/// order the legacy app writes on insert (Mongoose defaults first, then the create route's fields, <c>__v</c>);
/// initializers are the Mongoose defaults, applied on insert and when an old document lacks the field (§4.4).
/// Nullable members are left out when <c>null</c>, like <c>undefined</c> in Mongoose.
/// </summary>
public sealed class BudgetDocument
{
    public ObjectId Id { get; set; }

    public double Revision { get; set; }

    public bool Complete { get; set; }

    public bool WasPdfDownloaded { get; set; }

    public double Margin { get; set; } = 30;

    public double DaysNeededToDoTheWork { get; set; } = 60;

    public double Validity { get; set; } = 30;

    public bool HasGenerateJob { get; set; }

    /// <summary>→ <c>entities._id</c>.</summary>
    public ObjectId? Client { get; set; }

    public string? Descriptive { get; set; }

    public string? PersonInCharge { get; set; }

    /// <summary>→ <c>users._id</c>.</summary>
    public ObjectId? MadeBy { get; set; }

    /// <summary><c>"YY-NNNN"</c>.</summary>
    public string? BudgetNumber { get; set; }

    public string? PaymentMethod { get; set; }

    public double? Total { get; set; }

    /// <summary>The PDF text; missing in old budgets.</summary>
    public string? Description { get; set; }

    /// <summary>Mongoose default <c>Date.now</c>: set by the create use case from <c>TimeProvider</c>.</summary>
    public DateTime? Date { get; set; }

    /// <summary>Mongoose <c>__v</c>.</summary>
    public int Version { get; set; }

    /// <summary>Fields the new app does not know, kept as they are (§4.3 rule 7).</summary>
    public BsonDocument? ExtraElements { get; set; }
}
