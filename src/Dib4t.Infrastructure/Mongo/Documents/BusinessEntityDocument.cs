using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>entities</c> (legacy <c>models/entity.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class BusinessEntityDocument
{
    public ObjectId Id { get; set; }

    public string? Name { get; set; }

    public string? FiscalNumber { get; set; }

    public string? Address { get; set; }

    public string? ZipCode { get; set; }

    public string? Location { get; set; }

    public string? PaymentCondition { get; set; }

    /// <summary>One of <see cref="SharedKernel.Vocabulary.EntityTypes"/>.</summary>
    public string? Type { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
