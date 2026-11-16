using Dib4t.Application.Entities;
using Dib4t.Domain.Entities;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary><c>entities</c> (legacy <c>repositories/entityRepository.js</c>, the entity routes' <c>new Entity(body)</c> and <c>entity.set(…)</c> + <c>save</c>).</summary>
internal sealed class MongoEntityRepository(IMongoDatabase database) : IEntityRepository
{
    /// <summary>MongoDB's "Regular expression is invalid" (an <c>$regex</c> PCRE2 refuses).</summary>
    private const int InvalidRegexCode = 51091;

    private readonly IMongoCollection<BusinessEntityDocument> _entities = database.Collection<BusinessEntityDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Entities);

    private static SortDefinition<BusinessEntityDocument> ByName => Builders<BusinessEntityDocument>.Sort.Ascending(e => e.Name);

    public async Task<IReadOnlyList<BusinessEntity>> ListByNameAsync(CancellationToken cancellationToken)
    {
        var documents = await _entities.Find(FilterDefinition<BusinessEntityDocument>.Empty).Sort(ByName).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<BusinessEntity>> ListClientsAsync(CancellationToken cancellationToken)
    {
        // Legacy find({ type: [client, clientAndSupplier] }): Mongoose casts the array to $in.
        var filter = Builders<BusinessEntityDocument>.Filter.In(e => e.Type, [EntityTypes.Client, EntityTypes.ClientAndSupplier]);
        var documents = await _entities.Find(filter).Sort(ByName).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<BusinessEntity?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        var document = await _entities.Find(Builders<BusinessEntityDocument>.Filter.Eq(e => e.Id, ObjectId.Parse(id)))
            .Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    public async Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken) =>
        await _entities.Find(Builders<BusinessEntityDocument>.Filter.Eq(e => e.Name, name)).Limit(1).AnyAsync(cancellationToken);

    public async Task<BusinessEntity> InsertAsync(EntityInput entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // The legacy UI's order (entityService.newEntity). Mongoose itself follows the body's key order; the new app
        // always writes the UI's (no other API consumers, ADR-0004). A null is stored (the typed documents leave
        // nulls out), so this insert is written as BSON.
        var document = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", entity.Name } };
        foreach (var (field, text) in OptionalTexts(entity))
        {
            if (text.IsPresent)
            {
                document[field] = Bson(text)!;
            }
        }

        document["paymentCondition"] = entity.PaymentCondition;
        document["type"] = entity.Type;
        document["__v"] = 0;
        await _raw.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task<BusinessEntity?> UpdateAsync(string id, EntityInput entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        // entity.set({ name, fiscalNumber, address, zipCode, location, paymentCondition, type }), in the route's order.
        var changes = MongooseSave.Update(stored,
        [
            ("name", entity.Name),
            .. OptionalTexts(entity).Select(t => (t.Field, Bson(t.Text))),
            ("paymentCondition", entity.PaymentCondition),
            ("type", entity.Type),
        ]);
        if (changes is not null)
        {
            var result = await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
            {
                return null;
            }
        }

        return ToDomain(stored); // the saved document, as the legacy route answers it
    }

    public async Task<IReadOnlyList<BusinessEntity>?> SearchAsync(EntitySearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var filter = Builders<BsonDocument>.Filter.And(search.Patterns.Select(p =>
            Builders<BsonDocument>.Filter.Regex(p.Field, new BsonRegularExpression(p.Pattern, "i"))));
        try
        {
            var documents = await _raw.Find(filter).ToListAsync(cancellationToken);
            return [.. documents.Select(ToDomain)];
        }
        catch (MongoCommandException ex) when (ex.Code == InvalidRegexCode)
        {
            return null;
        }
    }

    private static IEnumerable<(string Field, EntityText Text)> OptionalTexts(EntityInput entity) =>
    [
        ("fiscalNumber", entity.FiscalNumber),
        ("address", entity.Address),
        ("zipCode", entity.ZipCode),
        ("location", entity.Location),
    ];

    /// <summary>A text as MongooseSave assigns it: missing = <c>undefined</c> (C# <c>null</c>), <c>null</c> = BSON null.</summary>
    private static BsonValue? Bson(EntityText text) =>
        !text.IsPresent ? null : text.Value is null ? BsonNull.Value : (BsonValue)text.Value;

    private static BusinessEntity ToDomain(BsonDocument document) => ToDomain(BsonSerializer.Deserialize<BusinessEntityDocument>(document));

    private static BusinessEntity ToDomain(BusinessEntityDocument document) => new(
        document.Id.ToString(),
        document.Name,
        document.FiscalNumber,
        document.Address,
        document.ZipCode,
        document.Location,
        document.PaymentCondition,
        document.Type,
        document.Version);
}
