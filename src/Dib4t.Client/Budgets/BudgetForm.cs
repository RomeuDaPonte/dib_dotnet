using System.Text.RegularExpressions;
using Dib4t.Client.Forms;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;

namespace Dib4t.Client.Budgets;

/// <summary>What a budget dialog does.</summary>
public enum BudgetDialogMode
{
    /// <summary>"Novo orçamento" (<c>newBudgetModdal.jsx</c> without a budget).</summary>
    New,

    /// <summary>"Editar orçamento" (<c>newBudgetModdal.jsx</c> with a budget): the client cannot change.</summary>
    Edit,

    /// <summary>"Clonar orçamento" (<c>cloneBudgetModalBody.jsx</c>).</summary>
    Clone,
}

/// <summary>
/// The budget dialogs (legacy <c>newBudgetModdal.jsx</c>, <c>cloneBudgetModalBody.jsx</c>) and their client-side
/// check, the legacy yup <c>newBudgetSchema</c> (<c>schemas/budget/newBudgetSchema.js</c>) with yup's and the
/// schema's own messages: client, descriptive (only the schema's characters, at most 30), person in charge and author
/// required. The schema's characters exclude <c>/</c>, so the D21 rule is met before the server checks it.
/// </summary>
public sealed partial class BudgetForm
{
    private BudgetForm(BudgetDialogMode mode, string? budgetId)
    {
        Mode = mode;
        BudgetId = budgetId;
    }

    private string? _clientId;
    private string? _clientName;

    public BudgetDialogMode Mode { get; }

    /// <summary>The edited or cloned budget; <c>null</c> for a new one.</summary>
    public string? BudgetId { get; }

    /// <summary>The legacy titles.</summary>
    public string Title => Mode switch
    {
        BudgetDialogMode.Edit => "Editar orçamento",
        BudgetDialogMode.Clone => "Clonar orçamento",
        _ => "Novo orçamento",
    };

    /// <summary>The submit button: "Clonar" in the clone dialog, else "Concluir".</summary>
    public string SubmitText => Mode == BudgetDialogMode.Clone ? "Clonar" : "Concluir";

    /// <summary>The client (Autocomplete): only in the new and clone dialogs.</summary>
    public bool CanChangeClient => Mode != BudgetDialogMode.Edit;

    public EntityResponse? Client { get; set; }

    public string? Descriptive { get; set; }

    public string? PersonInCharge { get; set; }

    public string? MadeById { get; set; }

    /// <summary>New: empty, "Realizado por" = the signed-in user.</summary>
    public static BudgetForm New(string currentUserId) => new(BudgetDialogMode.New, null)
    {
        Descriptive = string.Empty,
        PersonInCharge = string.Empty,
        MadeById = currentUserId,
    };

    /// <summary>Edit: the budget's values; the client is the budget's (shown, not editable).</summary>
    public static BudgetForm Edit(BudgetResponse budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return new BudgetForm(BudgetDialogMode.Edit, budget.Id)
        {
            Client = budget.Client,
            _clientId = budget.Client?.Id,
            Descriptive = budget.Descriptive ?? string.Empty,
            PersonInCharge = budget.PersonInCharge ?? string.Empty,
            MadeById = budget.MadeBy?.Id,
        };
    }

    /// <summary>
    /// Clone: the row's descriptive and person in charge; the client is found by its <b>name</b> among the clients
    /// once they are loaded (legacy <c>clientsName.find(cn =&gt; cn.name === budget.clientName)</c>, see
    /// <see cref="ResolveClient"/>); "Realizado por" = the signed-in user.
    /// </summary>
    public static BudgetForm Clone(BudgetListItemResponse budget, string currentUserId)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return new BudgetForm(BudgetDialogMode.Clone, budget.Id)
        {
            _clientName = budget.ClientName,
            Descriptive = budget.Descriptive ?? string.Empty,
            PersonInCharge = budget.PersonInCharge ?? string.Empty,
            MadeById = currentUserId,
        };
    }

    /// <summary>The client of the loaded list: by id (edit) or by name (clone, legacy rule); a new form keeps none.</summary>
    public void ResolveClient(IReadOnlyList<EntityResponse> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (_clientId is not null)
        {
            Client = clients.FirstOrDefault(c => c.Id == _clientId) ?? Client;
        }
        else if (Mode == BudgetDialogMode.Clone && Client is null)
        {
            Client = clients.FirstOrDefault(c => string.Equals(c.Name, _clientName, StringComparison.Ordinal));
        }
    }

    /// <summary>yup: <c>required()</c> first, then the descriptive's <c>matches</c> and <c>max(30)</c> (JavaScript length).</summary>
    public BudgetFormErrors Validate()
    {
        string? descriptive = null;
        if (string.IsNullOrEmpty(Descriptive))
        {
            descriptive = Yup.Required("descriptive");
        }
        else if (!DescriptiveCharacters().IsMatch(Descriptive))
        {
            descriptive = "Carácteres inválidos";
        }
        else if (Descriptive.Length > 30)
        {
            descriptive = "Não pode exceder mais de 25 caracteres"; // the schema's text (it says 25, the rule is 30)
        }

        return new BudgetFormErrors(
            Client is null ? Yup.Required("client") : null,
            descriptive,
            string.IsNullOrEmpty(PersonInCharge) ? Yup.Required("personInCharge") : null,
            string.IsNullOrEmpty(MadeById) ? Yup.Required("madeById") : null);
    }

    /// <summary>The body of <c>budgetService.newBudget</c> / <c>cloneBudget</c>.</summary>
    public NewBudgetRequest ToNewRequest() => new(Descriptive ?? string.Empty, PersonInCharge ?? string.Empty, MadeById ?? string.Empty, Client?.Id ?? string.Empty);

    /// <summary>The body of <c>budgetService.updateBudget</c>.</summary>
    public UpdateBudgetRequest ToUpdateRequest() => new(MadeById ?? string.Empty, Descriptive ?? string.Empty, PersonInCharge ?? string.Empty);

    /// <summary>
    /// MUI Autocomplete's default filter (<c>createFilterOptions</c>): the label contains the typed text, ignoring
    /// case and accents.
    /// </summary>
    public static IEnumerable<EntityResponse> FilterClients(IEnumerable<EntityResponse> clients, string? text)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (string.IsNullOrWhiteSpace(text))
        {
            return clients;
        }

        var wanted = Fold(text.Trim());
        return clients.Where(c => Fold(c.Name ?? string.Empty).Contains(wanted, StringComparison.Ordinal));
    }

    private static string Fold(string text) =>
        string.Concat(text.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark))
            .ToLowerInvariant();

    // The legacy pattern "^[a-zA-ZçÇã,.êÊàáÀÁéÉúÚÙù,0-9_ ]+$" (no flags; JavaScript's $ is the end of the text).
    [GeneratedRegex("^[a-zA-ZçÇã,.êÊàáÀÁéÉúÚÙù,0-9_ ]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptiveCharacters();
}

/// <summary>Per-field messages shown under the inputs.</summary>
public sealed record BudgetFormErrors(string? Client, string? Descriptive, string? PersonInCharge, string? MadeById)
{
    public static BudgetFormErrors None { get; } = new(null, null, null, null);

    public bool Any => Client is not null || Descriptive is not null || PersonInCharge is not null || MadeById is not null;
}
