using Dib4t.Infrastructure.Mongo.Documents;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>The legacy collection names (Mongoose pluralization, confirmed on production, PLAN.md §4.1).</summary>
public static class MongoCollectionNames
{
    public const string Budgets = "budgets";
    public const string Entities = "entities";
    public const string Jobs = "jobs";
    public const string Tasks = "tasks";
    public const string Users = "users";
    public const string Prices = "prices";
    public const string PaymentMethods = "paymentmethods";
    public const string PrinterSettings = "printersettings";

    public static IReadOnlyDictionary<Type, string> ByDocumentType { get; } = new Dictionary<Type, string>
    {
        [typeof(BudgetDocument)] = Budgets,
        [typeof(BusinessEntityDocument)] = Entities,
        [typeof(JobDocument)] = Jobs,
        [typeof(BudgetTaskDocument)] = Tasks,
        [typeof(UserDocument)] = Users,
        [typeof(PricesDocument)] = Prices,
        [typeof(PaymentMethodDocument)] = PaymentMethods,
        [typeof(PrinterSettingsDocument)] = PrinterSettings,
    };

    /// <summary>The collection of a document type, e.g. <c>database.Collection&lt;JobDocument&gt;()</c> → <c>jobs</c>.</summary>
    public static IMongoCollection<TDocument> Collection<TDocument>(this IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.GetCollection<TDocument>(ByDocumentType[typeof(TDocument)]);
    }
}
