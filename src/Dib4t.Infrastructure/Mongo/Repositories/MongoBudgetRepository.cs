using Dib4t.Application.Budgets;
using Dib4t.Domain.Budgets;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Infrastructure.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary>
/// <c>budgets</c> (legacy <c>repositories/budgetRepository.js</c>, the budget routes' <c>new Budget({…})</c> and
/// <c>budget.update</c> + <c>save</c>). The populated names and authors are read from <c>entities</c> and <c>users</c>
/// (never the password hash).
/// </summary>
internal sealed class MongoBudgetRepository(IMongoDatabase database) : IBudgetRepository
{
    /// <summary>MongoDB's "Regular expression is invalid" (an <c>$regex</c> PCRE2 refuses).</summary>
    private const int InvalidRegexCode = 51091;

    /// <summary>
    /// The static defaults of the budget schema (legacy <c>models/budget.js</c>): Mongoose applies them on read, so
    /// assigning a default to a document that lacks the field writes nothing.
    /// </summary>
    private static readonly Dictionary<string, BsonValue> SchemaDefaults = new(StringComparer.Ordinal)
    {
        ["revision"] = 0,
        ["complete"] = false,
        ["wasPdfDownloaded"] = false,
        ["margin"] = 30,
        ["daysNeededToDoTheWork"] = 60,
        ["validity"] = 30,
        ["hasGenerateJob"] = false,
    };

    private readonly IMongoCollection<BudgetDocument> _budgets = database.Collection<BudgetDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Budgets);
    private readonly IMongoCollection<BsonDocument> _entities = database.GetCollection<BsonDocument>(MongoCollectionNames.Entities);
    private readonly IMongoCollection<UserDocument> _users = database.Collection<UserDocument>();

    private static SortDefinition<BudgetDocument> NewestFirst =>
        Builders<BudgetDocument>.Sort.Descending(b => b.BudgetNumber).Descending(b => b.Revision);

    public async Task<IReadOnlyList<BudgetRow>> ListNewestAsync(int limit, CancellationToken cancellationToken)
    {
        var documents = await _budgets.Find(FilterDefinition<BudgetDocument>.Empty).Sort(NewestFirst).Limit(limit).ToListAsync(cancellationToken);
        var clients = await NamesAsync(_entities, documents.Select(d => d.Client), cancellationToken);
        var authors = await NamesAsync(database.GetCollection<BsonDocument>(MongoCollectionNames.Users), documents.Select(d => d.MadeBy), cancellationToken);
        return
        [
            .. documents.Select(d => new BudgetRow(
                ToDomain(d),
                d.Client is { } client ? clients.GetValueOrDefault(client) : null,
                d.MadeBy is { } author ? authors.GetValueOrDefault(author) : null)),
        ];
    }

    public async Task<(IReadOnlyList<BudgetRow>? Rows, string? Refusal)> SearchAsync(string pattern, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = new BsonRegularExpression(pattern, "i");
        // The legacy pipeline, stage for stage (budgetRepository.searchBudgets).
        BsonDocument[] pipeline =
        [
            new("$lookup", new BsonDocument { { "from", MongoCollectionNames.Entities }, { "localField", "client" }, { "foreignField", "_id" }, { "as", "client" } }),
            new("$lookup", new BsonDocument { { "from", MongoCollectionNames.Users }, { "localField", "madeBy" }, { "foreignField", "_id" }, { "as", "madeBy" } }),
            new("$unwind", "$client"),
            new("$unwind", "$madeBy"),
            new("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("budgetNumber", new BsonDocument("$regex", regex)),
                new BsonDocument("client.name", new BsonDocument("$regex", regex)),
                new BsonDocument("descriptive", new BsonDocument("$regex", regex)),
                new BsonDocument("personInCharge", new BsonDocument("$regex", regex)),
                new BsonDocument("madeBy.name", new BsonDocument("$regex", regex)),
            })),
            new("$sort", new BsonDocument { { "budgetNumber", -1 }, { "revision", -1 } }),
            // Only what a row shows: the joined documents' names (never the password hash).
            new("$addFields", new BsonDocument { { "client", "$client.name" }, { "madeBy", "$madeBy.name" } }),
        ];
        try
        {
            var found = await _raw.Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).ToListAsync(cancellationToken);
            return ([.. found.Select(ToRow)], null);
        }
        catch (MongoCommandException ex) when (ex.Code == InvalidRegexCode)
        {
            return (null, ex.ErrorMessage);
        }
    }

    public async Task<Budget?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        var document = await _budgets.Find(Builders<BudgetDocument>.Filter.Eq(b => b.Id, ObjectId.Parse(id))).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    public async Task<IReadOnlyList<Budget>> ListRevisionsAsync(string budgetNumber, CancellationToken cancellationToken)
    {
        var documents = await _budgets.Find(Builders<BudgetDocument>.Filter.Eq(b => b.BudgetNumber, budgetNumber))
            .Sort(Builders<BudgetDocument>.Sort.Descending(b => b.Revision)).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<int> CountRevisionsAsync(string budgetNumber, CancellationToken cancellationToken) =>
        (int)await _budgets.CountDocumentsAsync(Builders<BudgetDocument>.Filter.Eq(b => b.BudgetNumber, budgetNumber), cancellationToken: cancellationToken);

    public async Task<int> CountNumbersSinceAsync(DateTime from, CancellationToken cancellationToken)
    {
        // Legacy Budget.find({ date: { $gte: new Date("YYYY") } }).distinct("budgetNumber").
        var numbers = await _raw.DistinctAsync<BsonValue>("budgetNumber", new BsonDocument("date", new BsonDocument("$gte", from)), cancellationToken: cancellationToken);
        return (await numbers.ToListAsync(cancellationToken)).Count;
    }

    public async Task<bool> NumberExistsAsync(string budgetNumber, CancellationToken cancellationToken) =>
        await _budgets.Find(Builders<BudgetDocument>.Filter.Eq(b => b.BudgetNumber, budgetNumber)).Limit(1).AnyAsync(cancellationToken);

    public async Task<BudgetAuthor?> FindAuthorAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await _users.Find(Builders<UserDocument>.Filter.Eq(u => u.Id, ObjectId.Parse(userId)))
            .Project<UserDocument>(Builders<UserDocument>.Projection.Exclude(u => u.Password)).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return user is null ? null : new BudgetAuthor(user.Id.ToString(), user.Name, user.Email, user.Roles, user.FieldOfCompetencies, user.Version);
    }

    public async Task<Budget> InsertAsync(NewBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        // new Budget({…}) of the routes: the defaulted paths first (schema order), then client … total, description,
        // date, __v (BudgetDocument's member order); a missing value (undefined) is not written.
        var document = new BudgetDocument
        {
            Id = ObjectId.GenerateNewId(),
            Revision = budget.Revision,
            Margin = budget.Margin,
            DaysNeededToDoTheWork = budget.DaysNeededToDoTheWork,
            Validity = budget.Validity,
            Client = ObjectId.Parse(budget.ClientId),
            Descriptive = budget.Descriptive,
            PersonInCharge = budget.PersonInCharge,
            MadeBy = ObjectId.Parse(budget.MadeById),
            BudgetNumber = budget.BudgetNumber,
            PaymentMethod = budget.PaymentMethod,
            Total = budget.Total,
            Description = budget.Description,
            Date = budget.Date,
            Version = 0,
        };
        await _budgets.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task<Budget?> UpdateAsync(string id, BudgetEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        // budget.update(newValues, madeBy), in the method's order.
        var changes = MongooseSave.Update(stored,
        [
            ("madeBy", ObjectId.Parse(edit.MadeById)),
            ("descriptive", edit.Descriptive),
            ("personInCharge", edit.PersonInCharge),
            ("complete", false),
            ("wasPdfDownloaded", false),
        ], SchemaDefaults);
        if (changes is not null)
        {
            var result = await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
            {
                return null;
            }
        }

        return ToDomain(BsonSerializer.Deserialize<BudgetDocument>(stored));
    }

    public Task<Budget?> PatchAsync(string id, BudgetPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        BsonValue value = patch switch
        {
            { Number: { } number } => JsNumberSerializer.ToBsonValue(number),
            { Text: { } text } => text,
            { Flag: { } flag } => flag,
            _ => throw new ArgumentException("A patch needs a value.", nameof(patch)),
        };

        // budget.patch(propertyName, value): the flags first, then the property (the final values are compared).
        return SaveAsync(id, [("complete", false), ("wasPdfDownloaded", false), (patch.Field, value)], cancellationToken);
    }

    public Task<Budget?> SetPdfDownloadedAsync(string id, CancellationToken cancellationToken) =>
        SaveAsync(id, [("wasPdfDownloaded", true)], cancellationToken);

    public Task SetIncompleteAsync(string id, CancellationToken cancellationToken) =>
        SaveAsync(id, [("complete", false), ("wasPdfDownloaded", false)], cancellationToken);

    public Task<Budget?> ImportAsync(string id, BudgetImport import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);
        // D24: flags reset like any other change, then what was imported; nothing is removed.
        var assignments = new List<(string Field, BsonValue? Value)> { ("complete", false), ("wasPdfDownloaded", false) };
        if (import.Total is { } total)
        {
            assignments.Add(("total", JsNumberSerializer.ToBsonValue(total)));
        }

        if (import.Description is { } text)
        {
            assignments.Add(("description", text));
        }

        return SaveAsync(id, assignments, cancellationToken);
    }

    /// <summary>Assignments to a stored budget + <c>save()</c> (<see cref="MongooseSave"/>); the budget as saved, or <c>null</c>.</summary>
    private async Task<Budget?> SaveAsync(string id, IReadOnlyList<(string Field, BsonValue? Value)> assignments, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var changes = MongooseSave.Update(stored, assignments, SchemaDefaults, keepEquivalentTexts: true);
        if (changes is not null)
        {
            var result = await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
            {
                return null;
            }
        }

        return ToDomain(BsonSerializer.Deserialize<BudgetDocument>(stored));
    }

    /// <summary>The <c>name</c> of each referenced document (legacy <c>populate</c>, only the name is used).</summary>
    private static async Task<Dictionary<ObjectId, string?>> NamesAsync(
        IMongoCollection<BsonDocument> collection, IEnumerable<ObjectId?> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.OfType<ObjectId>().Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var documents = await collection.Find(Builders<BsonDocument>.Filter.In("_id", distinct))
            .Project(Builders<BsonDocument>.Projection.Include("name")).ToListAsync(cancellationToken);
        return documents.ToDictionary(d => d["_id"].AsObjectId, d => d.TryGetValue("name", out var name) && name.IsString ? name.AsString : null);
    }

    private static BudgetRow ToRow(BsonDocument found)
    {
        var client = found["client"];
        var author = found["madeBy"];
        found.Remove("client");
        found.Remove("madeBy");
        return new BudgetRow(ToDomain(BsonSerializer.Deserialize<BudgetDocument>(found)), client.IsString ? client.AsString : null, author.IsString ? author.AsString : null);
    }

    internal static Budget ToDomain(BudgetDocument document) => new(
        document.Id.ToString(),
        document.Revision,
        document.Complete,
        document.WasPdfDownloaded,
        document.Margin,
        document.DaysNeededToDoTheWork,
        document.Validity,
        document.HasGenerateJob,
        document.Client?.ToString(),
        document.Descriptive,
        document.PersonInCharge,
        document.MadeBy?.ToString(),
        document.BudgetNumber,
        document.PaymentMethod,
        document.Total,
        document.Description,
        document.Date,
        document.Version);
}
