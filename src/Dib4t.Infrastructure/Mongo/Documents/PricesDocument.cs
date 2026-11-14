using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>prices</c>, a single document (legacy <c>models/prices.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class PricesDocument
{
    public ObjectId Id { get; set; }

    public double? Automation { get; set; }

    public double? Consultancy { get; set; }

    public double? Development { get; set; }

    /// <summary>1–100.</summary>
    public double? Margin { get; set; }

    public double? Machining { get; set; }

    public double? Assembly { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
