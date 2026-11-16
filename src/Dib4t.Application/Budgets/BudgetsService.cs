using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Application.Entities;
using Dib4t.Application.Settings;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Application.Budgets;

/// <summary>
/// The budget routes of legacy <c>routes/budget.js</c> for the budget list (every route T: any signed-in user, as
/// legacy; PLAN.md §6, §7.1): list, search, one budget, its revisions, create, clone, new revision, edit. Same order
/// of checks and texts as the legacy routes; a failure legacy leaves to Express (cast error, refused save, missing
/// prices) is a 500 <see cref="LegacyMessages.SomethingFailed"/> (D16), with the reason in the log only. Decided
/// fixes: D6 (the default task is created right after the budget, before any NAS step), D7 (the edit is validated),
/// D9 (a pattern MongoDB refuses is a 400), D21 (no <c>/</c> in a new descriptive), the numbering guard (§4.7).
/// </summary>
public sealed partial class BudgetsService(
    IBudgetRepository budgets,
    IBudgetTaskRepository tasks,
    IEntityRepository entities,
    IPricesRepository prices,
    BudgetNumbering numbering,
    TimeProvider time,
    IOptions<AppOptions> options,
    ILogger<BudgetsService> logger)
{
    /// <summary>Legacy <c>find().sort(…).limit(200)</c>: the list reads the 200 newest documents (all revisions).</summary>
    public const int ListLimit = 200;

    /// <summary><c>GET /</c>: the latest revision of each number among the 200 newest documents.</summary>
    public async Task<IReadOnlyList<BudgetRow>> ListAsync(CancellationToken cancellationToken) =>
        LatestRevisions(await budgets.ListNewestAsync(ListLimit, cancellationToken));

    /// <summary>
    /// <c>GET /search?q=</c>: Joi (400) → the text as a case-insensitive regular expression over number, client,
    /// descriptive, person in charge and author (D9: a pattern that cannot be used is a 400 "Error performing
    /// search: …"; legacy 500) → the latest matching revision of each number.
    /// </summary>
    public async Task<Result<IReadOnlyList<BudgetRow>>> SearchAsync(JsonElement query, CancellationToken cancellationToken)
    {
        var validated = BudgetValidators.ValidateSearch(query);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var pattern = validated.Value;
        if (pattern.Contains('\0', StringComparison.Ordinal))
        {
            // The legacy driver's text (js-bson 1.x), also the legacy answer's.
            return SearchRefused(pattern, $"value {pattern} must not contain null bytes");
        }

        var (rows, refusal) = await budgets.SearchAsync(pattern, cancellationToken);
        return rows is null ? SearchRefused(pattern, refusal ?? "invalid regular expression") : Result<IReadOnlyList<BudgetRow>>.Success(LatestRevisions(rows));
    }

    /// <summary><c>GET /getById/:id</c>: id cast (500) → unknown (400 <see cref="BudgetMessages.NotFound"/>) → populated budget.</summary>
    public async Task<Result<BudgetDetails>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var found = await FindAsync(id, BudgetMessages.NotFound, cancellationToken);
        return found.IsSuccess ? await DetailsAsync(found.Value, cancellationToken) : found.Error;
    }

    /// <summary><c>GET /revisions/:budgetNumber</c>: every revision, highest first, with its author.</summary>
    public async Task<IReadOnlyList<(Budget Budget, BudgetAuthor? MadeBy)>> ListRevisionsAsync(string budgetNumber, CancellationToken cancellationToken)
    {
        var revisions = await budgets.ListRevisionsAsync(budgetNumber, cancellationToken);
        var authors = new Dictionary<string, BudgetAuthor?>(StringComparer.Ordinal);
        var result = new List<(Budget, BudgetAuthor?)>(revisions.Count);
        foreach (var revision in revisions)
        {
            BudgetAuthor? author = null;
            if (revision.MadeById is { } userId && !authors.TryGetValue(userId, out author))
            {
                authors[userId] = author = await budgets.FindAuthorAsync(userId, cancellationToken);
            }

            result.Add((revision, author));
        }

        return result;
    }

    /// <summary>
    /// <c>POST /</c>: Joi (400) → D21 (400) → client (400 <see cref="BudgetMessages.ClientMissing"/>) → a supplier
    /// is a <b>200</b> notice and nothing is created → author (400 <see cref="BudgetMessages.AuthorMissing"/>) →
    /// prices → number (lock + guard) → budget (margin from the prices, payment method from the client, total of one
    /// automation hour) → default task (D6).
    /// </summary>
    public async Task<Result<BudgetSaved>> CreateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetValidators.ValidateNew(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var checkedBody = await CheckNewBudgetAsync(validated.Value, cancellationToken);
        if (!checkedBody.IsSuccess)
        {
            return checkedBody.Error;
        }

        if (checkedBody.Value.Notice is { } notice)
        {
            return BudgetSaved.Refused(notice);
        }

        var (input, client, author, _) = checkedBody.Value;
        var current = await prices.FindAsync(cancellationToken);
        if (current is not { Margin: { } margin, Automation: { } automation })
        {
            return NotSaved("create", input.Descriptive, "no prices document (or no margin/automation price): legacy TypeError");
        }

        if (string.IsNullOrEmpty(client!.PaymentCondition))
        {
            return NotSaved("create", input.Descriptive, "the client has no payment condition: Path `paymentMethod` is required");
        }

        var budget = await numbering.RunExclusiveAsync(async () =>
        {
            var number = await numbering.NextAsync(budgets, CurrentYear(), cancellationToken);
            return await budgets.InsertAsync(
                new NewBudget(0, client.Id, input.Descriptive, input.PersonInCharge, author!.Id, number, client.PaymentCondition, margin,
                    BudgetTotals.NewBudgetTotal(margin, automation), 60, 30, null, Now()),
                cancellationToken);
        }, cancellationToken);
        await tasks.InsertDefaultAsync(budget.Id, automation, cancellationToken);
        LogCreated(budget.Id, budget.BudgetNumber, budget.Revision, budget.Descriptive, client.Name);
        return BudgetSaved.Of(new BudgetDetails(budget, client, author));
    }

    /// <summary>
    /// <c>POST /clone/:id</c>: Joi (400) → D21 (400) → id cast (500) → budget (400 <see cref="BudgetMessages.Missing"/>)
    /// → client (400) → supplier (200 notice) → author (400) → a new number with the source's payment method, margin,
    /// total, days, validity and description, and copies of its tasks.
    /// </summary>
    public async Task<Result<BudgetSaved>> CloneAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetValidators.ValidateNew(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var source = await FindAsync(id, BudgetMessages.Missing, cancellationToken);
        if (!source.IsSuccess)
        {
            return source.Error;
        }

        var checkedBody = await CheckNewBudgetAsync(validated.Value, cancellationToken);
        if (!checkedBody.IsSuccess)
        {
            return checkedBody.Error;
        }

        if (checkedBody.Value.Notice is { } notice)
        {
            return BudgetSaved.Refused(notice);
        }

        var (input, client, author, _) = checkedBody.Value;
        var from = source.Value;
        if (string.IsNullOrEmpty(from.PaymentMethod))
        {
            return NotSaved("clone", from.Id, "the source has no payment method: Path `paymentMethod` is required");
        }

        var sourceTasks = await tasks.ListByBudgetAsync(from.Id, cancellationToken);
        var budget = await numbering.RunExclusiveAsync(async () =>
        {
            var number = await numbering.NextAsync(budgets, CurrentYear(), cancellationToken);
            return await budgets.InsertAsync(
                new NewBudget(0, client!.Id, input.Descriptive, input.PersonInCharge, author!.Id, number, from.PaymentMethod, from.Margin,
                    from.Total, from.DaysNeededToDoTheWork, from.Validity, from.Description, Now()),
                cancellationToken);
        }, cancellationToken);
        await tasks.InsertCopiesAsync(budget.Id, sourceTasks, cancellationToken);
        LogCloned(budget.Id, budget.BudgetNumber, from.Id, from.BudgetNumber, sourceTasks.Count);
        return BudgetSaved.Of(new BudgetDetails(budget, client, author));
    }

    /// <summary>
    /// <c>POST /newrevision/:id</c>: id cast (500) → budget (400 <see cref="BudgetMessages.Missing"/>) → the same
    /// number with <c>revision</c> = the number of documents with it, everything else copied (date now, flags
    /// reset), and copies of its tasks. Like legacy, the server does not check the menu's rule (complete, no job).
    /// </summary>
    public async Task<Result<BudgetDetails>> CreateRevisionAsync(string id, CancellationToken cancellationToken)
    {
        var source = await FindAsync(id, BudgetMessages.Missing, cancellationToken);
        if (!source.IsSuccess)
        {
            return source.Error;
        }

        var from = source.Value;
        if (from.ClientId is null || from.MadeById is null || from.BudgetNumber is null || string.IsNullOrEmpty(from.PaymentMethod)
            || string.IsNullOrEmpty(from.Descriptive))
        {
            return NotSaved("new revision", from.Id, "the source lacks a required field (client, madeBy, budgetNumber, paymentMethod, descriptive)");
        }

        var sourceTasks = await tasks.ListByBudgetAsync(from.Id, cancellationToken);
        var revision = await numbering.RunExclusiveAsync(async () =>
        {
            var count = await budgets.CountRevisionsAsync(from.BudgetNumber, cancellationToken);
            return await budgets.InsertAsync(
                new NewBudget(count, from.ClientId, from.Descriptive, from.PersonInCharge, from.MadeById, from.BudgetNumber, from.PaymentMethod,
                    from.Margin, from.Total, from.DaysNeededToDoTheWork, from.Validity, from.Description, Now()),
                cancellationToken);
        }, cancellationToken);
        await tasks.InsertCopiesAsync(revision.Id, sourceTasks, cancellationToken);
        LogRevised(revision.Id, revision.BudgetNumber, revision.Revision, from.Id, sourceTasks.Count);
        return await DetailsAsync(revision, cancellationToken);
    }

    /// <summary>
    /// <c>PUT /:id</c>: Joi (D7, 400) → id cast (500) → budget (400 <see cref="BudgetMessages.Missing"/>) → author
    /// (400 <see cref="BudgetMessages.AuthorMissing"/>) → a budget with a job is answered unchanged → D21 (400, only
    /// for a changed descriptive) → save (descriptive, person in charge, author; complete and PDF flags reset).
    /// </summary>
    public async Task<Result<BudgetDetails>> UpdateAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var validated = BudgetValidators.ValidateUpdate(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var found = await FindAsync(id, BudgetMessages.Missing, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var input = validated.Value;
        var author = await budgets.FindAuthorAsync(input.MadeById, cancellationToken);
        if (author is null)
        {
            return Error.Validation(BudgetMessages.AuthorMissing);
        }

        var budget = found.Value;
        if (budget.HasGenerateJob)
        {
            LogLocked(budget.Id, budget.BudgetNumber);
            return await DetailsAsync(budget, cancellationToken);
        }

        if (!string.Equals(budget.Descriptive, input.Descriptive, StringComparison.Ordinal) && BudgetValidators.HasSlash(input.Descriptive))
        {
            return Error.Validation(BudgetMessages.DescriptiveWithSlash);
        }

        var saved = await budgets.UpdateAsync(budget.Id, new BudgetEdit(author.Id, input.Descriptive, input.PersonInCharge), cancellationToken);
        if (saved is null)
        {
            return Error.Validation(BudgetMessages.Missing); // deleted meanwhile
        }

        LogUpdated(saved.Id, saved.BudgetNumber, saved.Revision, saved.Descriptive);
        var client = saved.ClientId is null ? null : await entities.FindByIdAsync(saved.ClientId, cancellationToken);
        return new BudgetDetails(saved, client, author);
    }

    /// <summary>Legacy dedupe: the first row of each number (rows come sorted by number, then revision, descending).</summary>
    private static IReadOnlyList<BudgetRow> LatestRevisions(IReadOnlyList<BudgetRow> rows)
    {
        var seen = new HashSet<string?>(StringComparer.Ordinal);
        return [.. rows.Where(row => seen.Add(row.Budget.BudgetNumber))];
    }

    /// <summary>Client → supplier notice → author (the shared checks of create and clone, after Joi and D21).</summary>
    private async Task<Result<CheckedBody>> CheckNewBudgetAsync(NewBudgetInput input, CancellationToken cancellationToken)
    {
        var client = await entities.FindByIdAsync(input.ClientId, cancellationToken);
        if (client is null)
        {
            return Error.Validation(BudgetMessages.ClientMissing);
        }

        if (client.Type == EntityTypes.Supplier)
        {
            LogSupplier(client.Id, client.Name);
            return new CheckedBody(input, client, null, BudgetMessages.ClientIsSupplier);
        }

        var author = await budgets.FindAuthorAsync(input.MadeById, cancellationToken);
        return author is null ? Error.Validation(BudgetMessages.AuthorMissing) : new CheckedBody(input, client, author, null);
    }

    private async Task<Result<Budget>> FindAsync(string id, string missing, CancellationToken cancellationToken)
    {
        if (LegacyObjectId.Cast(id) is not { } objectId)
        {
            LogCastFailed(id);
            return Error.Failure(LegacyMessages.SomethingFailed);
        }

        var budget = await budgets.FindByIdAsync(objectId, cancellationToken);
        return budget is null ? Error.Validation(missing) : budget;
    }

    private async Task<BudgetDetails> DetailsAsync(Budget budget, CancellationToken cancellationToken)
    {
        BusinessEntity? client = budget.ClientId is null ? null : await entities.FindByIdAsync(budget.ClientId, cancellationToken);
        var author = budget.MadeById is null ? null : await budgets.FindAuthorAsync(budget.MadeById, cancellationToken);
        return new BudgetDetails(budget, client, author);
    }

    private int CurrentYear()
    {
        var zone = options.Value.FindTimeZone() ?? TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Year;
    }

    /// <summary>JavaScript <c>Date.now()</c>: UTC, millisecond precision.</summary>
    private DateTime Now()
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private Error SearchRefused(string pattern, string reason)
    {
        LogSearchRefused(pattern, reason);
        return Error.Validation(BudgetMessages.SearchFailedPrefix + reason);
    }

    private Error NotSaved(string action, string? subject, string reason)
    {
        LogNotSaved(action, subject, reason);
        return new Error(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
    }

    private sealed record CheckedBody(NewBudgetInput Input, BusinessEntity? Client, BudgetAuthor? Author, string? Notice);

    [LoggerMessage(EventId = 70, Level = LogLevel.Information, Message = "Budget {BudgetId} created: {BudgetNumber} R{Revision} {Descriptive} for {Client}")]
    private partial void LogCreated(string budgetId, string? budgetNumber, double revision, string? descriptive, string? client);

    [LoggerMessage(EventId = 71, Level = LogLevel.Information, Message = "Budget {BudgetId} cloned as {BudgetNumber} from {SourceId} ({SourceNumber}), {TaskCount} tasks copied")]
    private partial void LogCloned(string budgetId, string? budgetNumber, string sourceId, string? sourceNumber, int taskCount);

    [LoggerMessage(EventId = 72, Level = LogLevel.Information, Message = "Budget {BudgetId} is revision {Revision} of {BudgetNumber} (from {SourceId}), {TaskCount} tasks copied")]
    private partial void LogRevised(string budgetId, string? budgetNumber, double revision, string sourceId, int taskCount);

    [LoggerMessage(EventId = 73, Level = LogLevel.Information, Message = "Budget {BudgetId} updated: {BudgetNumber} R{Revision} {Descriptive}")]
    private partial void LogUpdated(string budgetId, string? budgetNumber, double revision, string? descriptive);

    [LoggerMessage(EventId = 74, Level = LogLevel.Information, Message = "Budget {BudgetId} ({BudgetNumber}) not updated: it has a job (answered unchanged, as legacy)")]
    private partial void LogLocked(string budgetId, string? budgetNumber);

    [LoggerMessage(EventId = 75, Level = LogLevel.Information, Message = "No budget for entity {EntityId} ({Name}): it is a supplier")]
    private partial void LogSupplier(string entityId, string? name);

    [LoggerMessage(EventId = 76, Level = LogLevel.Warning, Message = "Budget {Action} not saved ({Subject}): {Reason}")]
    private partial void LogNotSaved(string action, string? subject, string reason);

    [LoggerMessage(EventId = 77, Level = LogLevel.Warning, Message = "Not a budget id: {Id} (legacy cast error, 500)")]
    private partial void LogCastFailed(string id);

    [LoggerMessage(EventId = 78, Level = LogLevel.Warning, Message = "Budget search refused /{Pattern}/i: {Reason}")]
    private partial void LogSearchRefused(string pattern, string reason);
}

/// <summary>
/// The outcome of create and clone: the new budget, or the legacy <b>200</b> notice that nothing was created (the
/// client is a supplier).
/// </summary>
public sealed record BudgetSaved(BudgetDetails? Budget, string? Notice)
{
    public static BudgetSaved Of(BudgetDetails budget) => new(budget, null);

    public static BudgetSaved Refused(string notice) => new(null, notice);
}
