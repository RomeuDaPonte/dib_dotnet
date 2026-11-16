using Dib4t.Contracts.Budgets;
using Dib4t.SharedKernel;

namespace Dib4t.Client.Budgets;

/// <summary>
/// What a legacy controlled number input holds (<c>budgetGeneralDetails.jsx</c>, <c>task.jsx</c>): the server's number
/// until the user types, then the typed text (<c>target.value</c>). The page computes with it as JavaScript does
/// (<c>Number(text)</c>; a missing value is <c>NaN</c>) and sends it as it is, so the server casts the text exactly as
/// it casts the legacy page's.
/// </summary>
public readonly record struct JsInput(double? Number, string? Text)
{
    public static JsInput Of(double? number) => new(number, null);

    public static JsInput Typed(string? text) => new(null, text ?? string.Empty);

    /// <summary>The value JavaScript arithmetic sees.</summary>
    public double Value => Text is not null ? JsNumber.FromText(Text) : Number ?? double.NaN;

    /// <summary>What the input shows (React renders a number with its JavaScript text).</summary>
    public string Display => Text ?? (Number is { } number ? JsNumber.ToString(number) : string.Empty);

    /// <summary>What <c>JSON.stringify</c> sends: the text, the number (<c>NaN</c> → <c>null</c>), or nothing (<c>undefined</c>).</summary>
    public object? Json => Text is not null ? Text : Number is { } number && double.IsFinite(number) ? number : null;
}

/// <summary>
/// One task line of the budget details page (legacy <c>budgetDetails/task.jsx</c>): its own form state, saved on blur.
/// </summary>
public sealed class BudgetTaskRow
{
    /// <summary>The select's texts (legacy <c>taskSelectInput.jsx</c>).</summary>
    public static IReadOnlyDictionary<string, string> TypeNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["automation"] = "Automação",
        ["consultancy"] = "Consultoria",
        ["development"] = "Desenvolvimento",
        ["machining"] = "Maquinação",
        ["assembly"] = "Montagem",
        ["components"] = "Componentes",
        ["others"] = "Outros",
    };

    public BudgetTaskRow(BudgetTaskResponse task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Id = task.Id;
        BudgetId = task.BudgetId ?? string.Empty;
        Load(task);
    }

    public string Id { get; }

    public string BudgetId { get; }

    public string? TaskType { get; set; }

    public string? Description { get; set; }

    public JsInput Quantity { get; set; }

    public JsInput UnitPrice { get; set; }

    /// <summary><c>quantity * unitPrice</c> (the header's tasks total sums these).</summary>
    public double LineTotal => Quantity.Value * UnitPrice.Value;

    /// <summary>The line's total as legacy shows it: <c>{quantity * unitPrice} €</c>.</summary>
    public string LineTotalText => JsNumber.ToString(LineTotal) + " €";

    /// <summary>Legacy <c>taskService.updateTask</c>: <c>{ taskType, description, quantity, unitPrice }</c>.</summary>
    public UpdateTaskRequest ToUpdateRequest() => new(TaskType, Description, Quantity.Json, UnitPrice.Json);

    /// <summary>The saved task replaces the form (legacy <c>setTaskForm({ ...savedTask })</c>).</summary>
    public void Load(BudgetTaskResponse saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        TaskType = saved.TaskType;
        Description = saved.Description;
        Quantity = JsInput.Of(saved.Quantity);
        UnitPrice = JsInput.Of(saved.UnitPrice);
    }
}
