using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>paymentmethods</c> (legacy <c>models/paymentMethods.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class PaymentMethodDocument
{
    public ObjectId Id { get; set; }

    public string? Name { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
