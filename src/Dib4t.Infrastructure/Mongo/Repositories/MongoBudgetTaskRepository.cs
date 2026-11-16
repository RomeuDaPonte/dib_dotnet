using Dib4t.Application.Budgets;
using Dib4t.Domain.Budgets;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Infrastructure.Mongo.Serialization;
using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary><c>tasks</c> of a budget (legacy <c>repositories/taskRepository.js</c> and the task copies of <c>routes/budget.js</c>).</summary>
internal sealed class MongoBudgetTaskRepository(IMongoDatabase database) : IBudgetTaskRepository
{
    private readonly IMongoCollection<BudgetTaskDocument> _tasks = database.Collection<BudgetTaskDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Tasks);

    public async Task<IReadOnlyList<BudgetTask>> ListByBudgetAsync(string budgetId, CancellationToken cancellationToken)
    {
        var documents = await _tasks.Find(Builders<BudgetTaskDocument>.Filter.Eq(t => t.BudgetId, ObjectId.Parse(budgetId))).ToListAsync(cancellationToken);
        return [.. documents.Select(ToDomain)];
    }

    public async Task<BudgetTask> InsertDefaultAsync(string budgetId, double unitPrice, CancellationToken cancellationToken)
    {
        // createDefaultTask: new Task({ budgetId, taskType, description, unitPrice, quantity }) (BudgetTaskDocument's order).
        var document = new BudgetTaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            BudgetId = ObjectId.Parse(budgetId),
            TaskType = TaskTypes.Automation,
            Description = string.Empty,
            UnitPrice = unitPrice,
            Quantity = 1,
            Version = 0,
        };
        await _tasks.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task InsertCopiesAsync(string budgetId, IReadOnlyList<BudgetTask> tasks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            return; // Mongoose insertMany([]) sends nothing
        }

        // The copies' own order: new Task({ taskType, budgetId, description, unitPrice, quantity }), so taskType comes
        // before budgetId (unlike the default task); undefined values are not written.
        var budget = ObjectId.Parse(budgetId);
        var documents = tasks.Select(task =>
        {
            var document = new BsonDocument("_id", ObjectId.GenerateNewId());
            Add(document, "taskType", task.TaskType);
            document["budgetId"] = budget;
            Add(document, "description", task.Description);
            AddNumber(document, "unitPrice", task.UnitPrice);
            AddNumber(document, "quantity", task.Quantity);
            document["__v"] = 0;
            return document;
        });
        await _raw.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = true }, cancellationToken);
    }

    public async Task<BudgetTask?> FindByIdAsync(string taskId, CancellationToken cancellationToken)
    {
        var document = await _tasks.Find(Builders<BudgetTaskDocument>.Filter.Eq(t => t.Id, ObjectId.Parse(taskId))).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    public async Task<BudgetTask?> UpdateAsync(string taskId, TaskInput task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(taskId));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        // task.update(updatedTask), in the method's order.
        var changes = MongooseSave.Update(stored,
        [
            ("description", task.Description is { } description ? new BsonString(description) : null),
            ("unitPrice", JsNumberSerializer.ToBsonValue(task.UnitPrice)),
            ("quantity", JsNumberSerializer.ToBsonValue(task.Quantity)),
            ("taskType", task.TaskType),
        ], keepEquivalentTexts: true);
        if (changes is not null)
        {
            var result = await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
            {
                return null;
            }
        }

        return ToDomain(MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BudgetTaskDocument>(stored));
    }

    public async Task<BudgetTask> InsertAsync(string budgetId, TaskInput task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var document = new BudgetTaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            BudgetId = ObjectId.Parse(budgetId),
            TaskType = task.TaskType,
            Description = task.Description,
            UnitPrice = task.UnitPrice,
            Quantity = task.Quantity,
            Version = 0,
        };
        await _tasks.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document);
    }

    public async Task<BudgetTask?> DeleteAsync(string taskId, CancellationToken cancellationToken)
    {
        var document = await _tasks.FindOneAndDeleteAsync(Builders<BudgetTaskDocument>.Filter.Eq(t => t.Id, ObjectId.Parse(taskId)), cancellationToken: cancellationToken);
        return document is null ? null : ToDomain(document);
    }

    public async Task<long> DeleteByBudgetAsync(string budgetId, CancellationToken cancellationToken) =>
        (await _tasks.DeleteManyAsync(Builders<BudgetTaskDocument>.Filter.Eq(t => t.BudgetId, ObjectId.Parse(budgetId)), cancellationToken)).DeletedCount;

    private static void Add(BsonDocument document, string field, string? value)
    {
        if (value is not null)
        {
            document[field] = value;
        }
    }

    /// <summary>The js-bson 1.x number rule (PLAN.md §4.3 rule 1).</summary>
    private static void AddNumber(BsonDocument document, string field, double? value)
    {
        if (value is { } number)
        {
            document[field] = JsNumberSerializer.ToBsonValue(number);
        }
    }

    private static BudgetTask ToDomain(BudgetTaskDocument document) => new(
        document.Id.ToString(),
        document.BudgetId?.ToString(),
        document.TaskType,
        document.Description,
        document.UnitPrice,
        document.Quantity,
        document.Version);
}
