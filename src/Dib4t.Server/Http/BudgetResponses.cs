using Dib4t.Application.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;
using Dib4t.Domain.Entities;

namespace Dib4t.Server.Http;

/// <summary>A populated budget as the budget and job routes answer it (legacy <c>populate(["madeBy", "client"])</c>).</summary>
internal static class BudgetResponses
{
    public static BudgetResponse ToResponse(BudgetDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var b = details.Budget;
        return new BudgetResponse(b.Revision, b.Complete, b.WasPdfDownloaded, b.Margin, b.DaysNeededToDoTheWork, b.Validity, b.HasGenerateJob,
            b.Id, details.Client is { } client ? ToResponse(client) : null, b.Descriptive, b.PersonInCharge,
            details.MadeBy is { } author ? ToResponse(author) : null, b.BudgetNumber, b.PaymentMethod, b.Total, b.Description, b.Date, b.Version);
    }

    /// <summary>The author without the password hash (PLAN.md §6).</summary>
    public static BudgetAuthorResponse ToResponse(BudgetAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        return new(author.Roles, author.FieldOfCompetencies, author.Id, author.Name, author.Email, author.Version);
    }

    public static EntityResponse ToResponse(BusinessEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new(entity.Id, entity.Name, entity.FiscalNumber, entity.Address, entity.ZipCode, entity.Location, entity.PaymentCondition,
            entity.Type, entity.Version);
    }
}
