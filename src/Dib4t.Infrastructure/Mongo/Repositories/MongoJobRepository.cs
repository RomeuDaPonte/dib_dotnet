using Dib4t.Application.Budgets;
using Dib4t.Application.Jobs;
using Dib4t.Domain.Entities;
using Dib4t.Domain.Jobs;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Infrastructure.Mongo.Serialization;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary>
/// <c>jobs</c> (legacy <c>repositories/jobRepository.js</c> and the saves of <c>routes/job.js</c>). Reads work on the
/// BSON documents, so a field that is missing stays apart from one stored <c>null</c> (§4.5 item 2) and unknown
/// fields come along (<c>isHightPriority</c>, §4.5 item 4). The populated budget, client and author are read from
/// <c>budgets</c>, <c>entities</c> and <c>users</c> (never the password hash), one query per collection for a list.
/// </summary>
internal sealed class MongoJobRepository(IMongoDatabase database) : IJobRepository
{
    /// <summary>
    /// The static defaults of the job schema (legacy <c>models/job.js</c>): Mongoose applies them on read, so a
    /// document without the field reads (and answers) the default, and assigning the default writes nothing.
    /// </summary>
    private static readonly Dictionary<string, BsonValue> SchemaDefaults = new(StringComparer.Ordinal)
    {
        ["invoicedPercent"] = 0,
        ["finalPrice"] = 1,
        ["state"] = JobStates.Development,
        ["orderIdentification"] = "Por preencher",
        ["isOfficialOrderIdentification"] = false,
        ["components"] = 0,
        ["automation"] = 0,
        ["materials"] = 0,
        ["others"] = 0,
        ["costNonQuality"] = 0,
        ["commentType"] = new BsonArray(),
    };

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "_id", "invoicedPercent", "finalPrice", "state", "orderIdentification", "isOfficialOrderIdentification", "components", "automation",
        "materials", "others", "costNonQuality", "commentType", "budget", "createdBy", "dueDate", "startDate", "devStatistic",
        "machiningStatistic", "assemblyStatistic", "priority", "__v", "comments", "jobStatisticsDates", "deliveryDate", "invoicedBy",
    };

    private readonly IMongoCollection<JobDocument> _jobs = database.Collection<JobDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Jobs);
    private readonly IMongoCollection<BudgetDocument> _budgets = database.Collection<BudgetDocument>();
    private readonly IMongoCollection<BusinessEntityDocument> _entities = database.Collection<BusinessEntityDocument>();
    private readonly IMongoCollection<UserDocument> _users = database.Collection<UserDocument>();

    public async Task<Job> InsertAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        // new Job({ budget, createdBy, ...req.body }) + setNewStatistics(tasks): JobDocument's member order is the
        // legacy one (defaults, then budget, createdBy, dueDate, startDate, the statistics, priority, __v); the costs
        // are 0, deviation = budgeted hours, each statistic gets a new _id after its fields.
        var document = new JobDocument
        {
            Id = ObjectId.GenerateNewId(),
            FinalPrice = job.FinalPrice,
            State = job.State,
            OrderIdentification = job.OrderIdentification,
            IsOfficialOrderIdentification = job.IsOfficialOrderIdentification,
            Budget = ObjectId.Parse(job.BudgetId),
            CreatedBy = job.CreatedBy,
            DueDate = job.DueDate,
            StartDate = job.StartDate,
            DevStatistic = new JobDevStatisticDocument { BudgetedHours = job.DevelopmentHours, Deviation = job.DevelopmentHours },
            MachiningStatistic = new JobMachiningStatisticDocument { BudgetedHours = job.MachiningHours, Deviation = job.MachiningHours },
            AssemblyStatistic = new JobAssemblyStatisticDocument { BudgetedHours = job.AssemblyHours, Deviation = job.AssemblyHours },
            Priority = JobPriorities.Medium,
            Version = 0,
        };
        await _jobs.InsertOneAsync(document, cancellationToken: cancellationToken);
        return ToDomain(document.ToBsonDocument());
    }

    public async Task<PopulatedJob?> FindByIdAsync(string id, JobPopulation populate, CancellationToken cancellationToken)
    {
        var found = await _raw.Find(new BsonDocument("_id", ObjectId.Parse(id))).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (await PopulateAsync([found], populate, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<PopulatedJob>> ListAsync(JobFilter filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // sort({ dueDate: 1 }) as a stable sort of the natural order: a missing/null date first (MongoDB's order),
        // equal dates keep the collection's order (what the legacy golden file and a collection scan give).
        var found = await _raw.Find(Filter(filter)).ToListAsync(cancellationToken);
        var sorted = found.Select((document, index) => (document, index))
            .OrderBy(e => e.document.TryGetValue("dueDate", out var due) && due.IsValidDateTime ? due.AsBsonDateTime.MillisecondsSinceEpoch : long.MinValue)
            .ThenBy(e => e.index)
            .Select(e => e.document)
            .Take(limit)
            .ToList();
        return await PopulateAsync(sorted, JobPopulation.BudgetAndClient, cancellationToken);
    }

    public async Task<int> CountAsync(JobFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return (int)await _raw.CountDocumentsAsync(Filter(filter), cancellationToken: cancellationToken);
    }

    public async Task<PopulatedJob?> FindByBudgetNumberAsync(string budgetNumber, CancellationToken cancellationToken)
    {
        // Legacy: Job.find().populate("budget") and the first job (natural order) whose budget has the number.
        var budgetIds = await _budgets.Find(Builders<BudgetDocument>.Filter.Eq(b => b.BudgetNumber, budgetNumber))
            .Project(b => b.Id).ToListAsync(cancellationToken);
        if (budgetIds.Count == 0)
        {
            return null;
        }

        var found = await _raw.Find(Builders<BsonDocument>.Filter.In("budget", budgetIds)).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (await PopulateAsync([found], JobPopulation.Budget, cancellationToken))[0];
    }

    public async Task<string?> FindIdByBudgetIdAsync(string budgetId, CancellationToken cancellationToken)
    {
        var found = await _raw.Find(new BsonDocument("budget", ObjectId.Parse(budgetId)))
            .Project(Builders<BsonDocument>.Projection.Include("_id")).Limit(1).FirstOrDefaultAsync(cancellationToken);
        return found?["_id"].ToString();
    }

    public async Task<PopulatedJob> SaveAsync(PopulatedJob read, IReadOnlyList<JobAssignment> assignments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(assignments);
        var filter = new BsonDocument("_id", ObjectId.Parse(read.Job.Id));
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return read; // deleted meanwhile: Mongoose's save would match nothing either
        }

        // Mongoose's document after the assignments: the stored fields with the schema defaults (applied on read),
        // then each assignment (undefined removes the field, so an unset default is not answered).
        var answered = Hydrated(stored);
        var bson = assignments.Select(a => (a.Path, Value: ToBson(a.Value))).ToList();
        foreach (var (path, value) in bson)
        {
            if (value is null)
            {
                MongooseSave.Remove(answered, path);
            }
            else
            {
                MongooseSave.Set(answered, path, value);
            }
        }

        var written = stored.DeepClone().AsBsonDocument;
        var update = MongooseSave.Update(written, bson, SchemaDefaults);
        if (update is not null)
        {
            await _raw.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
            answered["__v"] = written.TryGetValue("__v", out var version) ? version : 0;
        }

        return read with { Job = ToDomain(answered) };
    }

    private static FilterDefinition<BsonDocument> Filter(JobFilter filter)
    {
        var builder = Builders<BsonDocument>.Filter;
        var result = builder.Empty;
        if (filter.States is { } states)
        {
            result &= builder.In("state", states);
        }

        if (filter.Excluded is { } excluded)
        {
            result &= builder.Nin("state", excluded);
        }

        return result;
    }

    /// <summary>Legacy <c>populate</c>: the budget (<c>null</c> when gone), then its client and author as asked.</summary>
    private async Task<IReadOnlyList<PopulatedJob>> PopulateAsync(IReadOnlyList<BsonDocument> found, JobPopulation populate, CancellationToken cancellationToken)
    {
        var jobs = found.Select(d => ToDomain(Hydrated(d))).ToList();
        var budgetIds = jobs.Select(j => j.BudgetId).OfType<string>().Distinct(StringComparer.Ordinal).Select(ObjectId.Parse).ToList();
        var budgets = budgetIds.Count == 0
            ? []
            : (await _budgets.Find(Builders<BudgetDocument>.Filter.In(b => b.Id, budgetIds)).ToListAsync(cancellationToken))
                .ToDictionary(b => b.Id.ToString(), MongoBudgetRepository.ToDomain, StringComparer.Ordinal);

        var clients = new Dictionary<string, BusinessEntity>(StringComparer.Ordinal);
        var authors = new Dictionary<string, BudgetAuthor>(StringComparer.Ordinal);
        if (populate != JobPopulation.Budget)
        {
            var clientIds = budgets.Values.Select(b => b.ClientId).OfType<string>().Distinct(StringComparer.Ordinal).Select(ObjectId.Parse).ToList();
            if (clientIds.Count > 0)
            {
                foreach (var entity in await _entities.Find(Builders<BusinessEntityDocument>.Filter.In(e => e.Id, clientIds)).ToListAsync(cancellationToken))
                {
                    clients[entity.Id.ToString()] = MongoEntityRepository.ToDomain(entity);
                }
            }
        }

        if (populate == JobPopulation.BudgetClientAndAuthor)
        {
            var authorIds = budgets.Values.Select(b => b.MadeById).OfType<string>().Distinct(StringComparer.Ordinal).Select(ObjectId.Parse).ToList();
            if (authorIds.Count > 0)
            {
                var users = await _users.Find(Builders<UserDocument>.Filter.In(u => u.Id, authorIds))
                    .Project<UserDocument>(Builders<UserDocument>.Projection.Exclude(u => u.Password)).ToListAsync(cancellationToken);
                foreach (var user in users)
                {
                    authors[user.Id.ToString()] = new BudgetAuthor(user.Id.ToString(), user.Name, user.Email, user.Roles, user.FieldOfCompetencies, user.Version);
                }
            }
        }

        return [.. jobs.Select(job =>
        {
            if (job.BudgetId is null || !budgets.TryGetValue(job.BudgetId, out var budget))
            {
                return new PopulatedJob(job, null, populate);
            }

            var client = budget.ClientId is { } clientId ? clients.GetValueOrDefault(clientId) : null;
            var author = budget.MadeById is { } authorId ? authors.GetValueOrDefault(authorId) : null;
            return new PopulatedJob(job, new BudgetDetails(budget, client, author), populate);
        })];
    }

    /// <summary>A stored job with the schema defaults Mongoose applies on read (missing top-level fields only).</summary>
    private static BsonDocument Hydrated(BsonDocument stored)
    {
        var document = stored.DeepClone().AsBsonDocument;
        foreach (var (field, value) in SchemaDefaults)
        {
            if (!document.Contains(field))
            {
                document[field] = value.DeepClone();
            }
        }

        return document;
    }

    /// <summary>An assigned value as the legacy driver stores it (C# <c>null</c> = <c>undefined</c>, removed by the save).</summary>
    private static BsonValue? ToBson(StoredValue value) => value switch
    {
        StoredValue.UndefinedValue => null,
        StoredValue.NullValue => BsonNull.Value,
        StoredValue.NewIdValue => ObjectId.GenerateNewId(),
        StoredValue.NumberValue number => JsNumberSerializer.ToBsonValue(number.Value),
        StoredValue.TextValue text => text.Value,
        StoredValue.BooleanValue flag => flag.Value,
        StoredValue.DateValue date => new BsonDateTime(DateTime.SpecifyKind(date.Value, DateTimeKind.Utc)),
        StoredValue.TextsValue texts => new BsonArray(texts.Values.Select(t => t is null ? BsonNull.Value : (BsonValue)t)),
        StoredValue.DocumentValue document => new BsonDocument(document.Fields
            .Select(f => (f.Key, Value: ToBson(f.Value)))
            .Where(f => f.Value is not null)
            .Select(f => new BsonElement(f.Key, f.Value))),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>A job document (as read, defaults applied) → the domain job; a missing field stays missing.</summary>
    internal static Job ToDomain(BsonDocument d) => new(
        d["_id"].ToString()!,
        StoredNumber(d, "invoicedPercent"),
        Number(d, "finalPrice", 1),
        Text(d, "state") ?? JobStates.Development,
        Text(d, "orderIdentification") ?? "Por preencher",
        d.TryGetValue("isOfficialOrderIdentification", out var official) && official.IsBoolean && official.AsBoolean,
        Number(d, "components", 0),
        Number(d, "automation", 0),
        Number(d, "materials", 0),
        Number(d, "others", 0),
        d.TryGetValue("costNonQuality", out var cost) ? (cost.IsNumeric ? cost.ToDouble() : null) : 0,
        d.TryGetValue("commentType", out var types) && types.IsBsonArray ? [.. types.AsBsonArray.Select(t => t.IsString ? t.AsString : null)] : [],
        d.TryGetValue("budget", out var budget) && budget.IsObjectId ? budget.AsObjectId.ToString() : null,
        Text(d, "createdBy"),
        Date(d, "dueDate"),
        Date(d, "startDate"),
        Sub(d, "devStatistic") is { } dev ? Hours(dev) : null,
        Sub(d, "machiningStatistic") is { } m
            ? new JobMachiningStatistic(Hours(m), Check(m, "verificationAndShapeControl"), Check(m, "dimensionalVerificationAndControl"),
                Check(m, "analysisOfTheDegreeOfFinish"), Text(m, "validatedBy"))
            : null,
        Sub(d, "assemblyStatistic") is { } a
            ? new JobAssemblyStatistic(Hours(a), Check(a, "pneumaticCircuits"), Check(a, "electricalBoardsAndCircuits"), Check(a, "kinematics"),
                Check(a, "programmingFeaturesPLC"), Check(a, "operator_equipmentSafetyConditions"), Check(a, "visualInspection"),
                Text(a, "validTryOnFirstTest") ?? QuestionResponses.DefaultFirstTestTry, Text(a, "validatedBy"))
            : null,
        Text(d, "priority"),
        d.TryGetValue("__v", out var version) && version.IsNumeric ? version.ToInt32() : 0)
    {
        Comments = d.TryGetValue("comments", out var comments) ? (comments.IsString ? Stored<string>.Of(comments.AsString) : Stored<string>.Null) : Stored<string>.Missing,
        StatisticsDates = Sub(d, "jobStatisticsDates") is { } dates
            ? new JobStatisticsDates(SubId(dates), StoredDate(dates, "devEndedInDate"), StoredDate(dates, "onApprovalEndedInDate"),
                StoredDate(dates, "approvedEndedInDate"), StoredDate(dates, "machiningEndedInDate"), StoredDate(dates, "assemblyEndedInDate"),
                StoredDate(dates, "forInvoicingEndedInDate"))
            : null,
        DeliveryDate = StoredDate(d, "deliveryDate"),
        InvoicedBy = Text(d, "invoicedBy"),
        Extra = [.. d.Where(e => !KnownFields.Contains(e.Name)).Select(e => new KeyValuePair<string, object?>(e.Name, Plain(e.Value)))],
    };

    private static JobHours Hours(BsonDocument s) =>
        new(Number(s, "numberOfHoursUsed", 0), Number(s, "budgetedHours", 0), Number(s, "deviation", 0), SubId(s));

    private static string Check(BsonDocument s, string field) => Text(s, field) ?? QuestionResponses.NotApplicable;

    private static BsonDocument? Sub(BsonDocument d, string field) =>
        d.TryGetValue(field, out var value) && value.IsBsonDocument ? value.AsBsonDocument : null;

    /// <summary>A sub-document's <c>_id</c>; Mongoose gives one that lacks it a new id on read.</summary>
    private static string SubId(BsonDocument s) => s.TryGetValue("_id", out var id) ? id.ToString()! : ObjectId.GenerateNewId().ToString();

    private static string? Text(BsonDocument d, string field) => d.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static double Number(BsonDocument d, string field, double fallback) =>
        d.TryGetValue(field, out var value) && value.IsNumeric ? value.ToDouble() : fallback;

    private static Stored<double> StoredNumber(BsonDocument d, string field) =>
        !d.TryGetValue(field, out var value) ? Stored<double>.Missing : value.IsNumeric ? Stored<double>.Of(value.ToDouble()) : Stored<double>.Null;

    private static DateTime? Date(BsonDocument d, string field) =>
        d.TryGetValue(field, out var value) && value.IsBsonDateTime ? ToUtc(value.AsBsonDateTime) : null;

    private static Stored<DateTime> StoredDate(BsonDocument d, string field) =>
        !d.TryGetValue(field, out var value) ? Stored<DateTime>.Missing : value.IsBsonDateTime ? Stored<DateTime>.Of(ToUtc(value.AsBsonDateTime)) : Stored<DateTime>.Null;

    /// <summary>A BSON date as UTC (also before 1970 and in the year 0022, §4.5 item 5).</summary>
    private static DateTime ToUtc(BsonDateTime date) => DateTime.UnixEpoch.AddMilliseconds(date.MillisecondsSinceEpoch);

    /// <summary>An unknown field's value as a JSON value (booleans, numbers, texts, dates; anything else its text).</summary>
    private static object? Plain(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double => value.ToDouble(),
        BsonType.String => value.AsString,
        BsonType.DateTime => ToUtc(value.AsBsonDateTime),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        _ => value.ToString(),
    };
}
