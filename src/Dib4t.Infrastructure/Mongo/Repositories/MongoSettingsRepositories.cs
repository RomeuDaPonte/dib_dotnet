using Dib4t.Application.Settings;
using Dib4t.Domain.Settings;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Infrastructure.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary>The single <c>prices</c> document (legacy <c>repositories/pricesRepository.js</c>, <c>routes/prices.js</c>).</summary>
internal sealed class MongoPricesRepository(IMongoDatabase database) : IPricesRepository
{
    private readonly IMongoCollection<PricesDocument> _prices = database.Collection<PricesDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Prices);

    public async Task<Prices?> FindAsync(CancellationToken cancellationToken)
    {
        var document = await _prices.Find(FilterDefinition<PricesDocument>.Empty).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null
            ? null
            : new Prices(document.Automation, document.Consultancy, document.Development, document.Machining, document.Assembly, document.Margin);
    }

    public Task InsertAsync(Prices prices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prices);
        // Legacy seed order: _id, automation, consultancy, development, margin, machining, assembly, __v.
        return _prices.InsertOneAsync(
            new PricesDocument
            {
                Id = ObjectId.GenerateNewId(),
                Automation = prices.Automation,
                Consultancy = prices.Consultancy,
                Development = prices.Development,
                Margin = prices.Margin,
                Machining = prices.Machining,
                Assembly = prices.Assembly,
                Version = 0,
            },
            cancellationToken: cancellationToken);
    }

    public async Task<bool> UpdateAsync(Prices prices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var stored = await _raw.Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return false;
        }

        // prices.set({ automation, consultancy, development, margin, machining, assembly }), in the route's order.
        var changes = MongooseSave.Update(stored,
        [
            ("automation", Number(prices.Automation)),
            ("consultancy", Number(prices.Consultancy)),
            ("development", Number(prices.Development)),
            ("margin", Number(prices.Margin)),
            ("machining", Number(prices.Machining)),
            ("assembly", Number(prices.Assembly)),
        ]);
        if (changes is not null)
        {
            await _raw.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", stored["_id"]), changes, cancellationToken: cancellationToken);
        }

        return true;
    }

    private static BsonValue? Number(double? value) => value is { } number ? JsNumberSerializer.ToBsonValue(number) : null;
}

/// <summary><c>paymentmethods</c> (legacy <c>repositories/paymentMethodRepositoy.js</c>).</summary>
internal sealed class MongoPaymentMethodRepository(IMongoDatabase database) : IPaymentMethodRepository
{
    private readonly IMongoCollection<PaymentMethodDocument> _methods = database.Collection<PaymentMethodDocument>();

    public async Task<IReadOnlyList<PaymentMethod>> ListAsync(CancellationToken cancellationToken)
    {
        var documents = await _methods.Find(FilterDefinition<PaymentMethodDocument>.Empty).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        await _methods.Find(Builders<PaymentMethodDocument>.Filter.Eq(m => m.Name, name)).Limit(1).AnyAsync(cancellationToken);

    public async Task<PaymentMethod?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        var document = await _methods.Find(ById(id)).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    public async Task<PaymentMethod> InsertAsync(string name, CancellationToken cancellationToken)
    {
        var document = new PaymentMethodDocument { Id = ObjectId.GenerateNewId(), Name = name, Version = 0 };
        await _methods.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task<PaymentMethod?> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var document = await _methods.FindOneAndDeleteAsync(ById(id), cancellationToken: cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    private static FilterDefinition<PaymentMethodDocument> ById(string id) =>
        Builders<PaymentMethodDocument>.Filter.Eq(m => m.Id, ObjectId.Parse(id));

    private static PaymentMethod ToDomain(PaymentMethodDocument document) => new(document.Id.ToString(), document.Name, document.Version);
}

/// <summary><c>printersettings</c> (legacy <c>repositories/printerSettingsRepository.js</c>).</summary>
internal sealed class MongoPrinterSettingsRepository(IMongoDatabase database) : IPrinterSettingsRepository
{
    private readonly IMongoCollection<PrinterSettingsDocument> _printers = database.Collection<PrinterSettingsDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.PrinterSettings);

    public async Task<IReadOnlyList<PrinterSettings>> ListAsync(CancellationToken cancellationToken)
    {
        var documents = await _printers.Find(FilterDefinition<PrinterSettingsDocument>.Empty).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<PrinterSettings>> ListByTemplateAsync(string templateName, CancellationToken cancellationToken)
    {
        var documents = await _printers.Find(Builders<PrinterSettingsDocument>.Filter.Eq(p => p.TemplateName, templateName))
            .ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<PrinterSettings> InsertAsync(PrinterInput printer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        // The legacy UI's order (Formik initialValues): ip, port, templateName. Mongoose itself follows the body's key
        // order; the new app always writes the UI's (no other API consumers, ADR-0004).
        var document = new PrinterSettingsDocument
        {
            Id = ObjectId.GenerateNewId(),
            Ip = printer.Ip,
            Port = printer.Port,
            TemplateName = printer.TemplateName,
            Version = 0,
        };
        await _printers.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task<PrinterSettings?> UpdateAsync(string id, PrinterInput printer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var changes = MongooseSave.Update(stored,
        [
            ("ip", printer.Ip),
            ("port", JsNumberSerializer.ToBsonValue(printer.Port)),
            ("templateName", printer.TemplateName),
        ]);
        if (changes is not null)
        {
            await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
        }

        return ToDomain(BsonSerializer.Deserialize<PrinterSettingsDocument>(stored)); // the saved document, as Mongoose answers it
    }

    public async Task<PrinterSettings?> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var document = await _printers.FindOneAndDeleteAsync(
            Builders<PrinterSettingsDocument>.Filter.Eq(p => p.Id, ObjectId.Parse(id)), cancellationToken: cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    private static PrinterSettings ToDomain(PrinterSettingsDocument document) =>
        new(document.Id.ToString(), document.Ip, document.Port, document.TemplateName, document.Version);
}
