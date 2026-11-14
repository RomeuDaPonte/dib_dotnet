using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>tasks</c> (legacy <c>models/task.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class BudgetTaskDocument
{
    public ObjectId Id { get; set; }

    /// <summary>→ <c>budgets._id</c>.</summary>
    public ObjectId? BudgetId { get; set; }

    /// <summary>One of <see cref="SharedKernel.Vocabulary.TaskTypes"/>.</summary>
    public string? TaskType { get; set; }

    public string? Description { get; set; }

    public double? UnitPrice { get; set; }

    public double? Quantity { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
