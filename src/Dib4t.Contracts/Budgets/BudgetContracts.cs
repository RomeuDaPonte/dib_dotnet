using System.Text.Json.Serialization;
using Dib4t.Contracts.Entities;

namespace Dib4t.Contracts.Budgets;

/// <summary>
/// One row of <c>GET /api/budget</c> and <c>GET /api/budget/search</c> (legacy <c>budgetRepository.js</c>: the latest
/// revision of each number, flattened), in the legacy key order. A missing value is left out.
/// </summary>
public sealed record BudgetListItemResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Descriptive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonInCharge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MadeBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date,
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    bool HasGenerateJob,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Total,
    double DaysNeededToDoTheWork);

/// <summary>
/// A populated <c>madeBy</c> (legacy <c>populate("madeBy")</c>): the user document without the password hash (the
/// legacy API sends the hash; the new app never does).
/// </summary>
public sealed record BudgetAuthorResponse(
    IReadOnlyList<string?> Roles,
    string FieldOfCompetencies,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonPropertyName("__v")] int Version);

/// <summary>
/// A budget with its populated client and author, as legacy answers <c>GET getById/:id</c>, <c>POST /</c>,
/// <c>POST clone/:id</c>, <c>POST newrevision/:id</c> and <c>PUT /:id</c> (Mongoose <c>toJSON</c>: the defaulted
/// paths first, then <c>_id</c> and the other fields). <see cref="Client"/>/<see cref="MadeBy"/> are <c>null</c> when
/// the referenced document no longer exists.
/// </summary>
public sealed record BudgetResponse(
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    double Margin,
    double DaysNeededToDoTheWork,
    double Validity,
    bool HasGenerateJob,
    [property: JsonPropertyName("_id")] string Id,
    EntityResponse? Client,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Descriptive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonInCharge,
    BudgetAuthorResponse? MadeBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Total,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date,
    [property: JsonPropertyName("__v")] int Version);

/// <summary>
/// One revision of <c>GET /api/budget/revisions/:budgetNumber</c>: like <see cref="BudgetResponse"/>, but only the
/// author is populated (<see cref="Client"/> is the entity id).
/// </summary>
public sealed record BudgetRevisionResponse(
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    double Margin,
    double DaysNeededToDoTheWork,
    double Validity,
    bool HasGenerateJob,
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Descriptive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonInCharge,
    BudgetAuthorResponse? MadeBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Total,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date,
    [property: JsonPropertyName("__v")] int Version);

/// <summary>
/// <c>POST /api/budget</c> and <c>POST /api/budget/clone/:id</c> body, as the legacy dialogs send it
/// (<c>newBudgetModdal.jsx</c>, <c>cloneBudgetModalBody.jsx</c>: the form values without <c>client</c>, plus
/// <c>clientId</c>).
/// </summary>
public sealed record NewBudgetRequest(string Descriptive, string PersonInCharge, string MadeById, string ClientId);

/// <summary><c>PUT /api/budget/:id</c> body, as legacy <c>budgetService.updateBudget</c> sends it.</summary>
public sealed record UpdateBudgetRequest(string MadeById, string Descriptive, string PersonInCharge);
