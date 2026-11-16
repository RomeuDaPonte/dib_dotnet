using Dib4t.Client.Api;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Settings;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Calculations;
using MudBlazor;

namespace Dib4t.Client.Budgets;

/// <summary>
/// The budget details page (legacy <c>budgetDetails/*.jsx</c>, <c>budgetContext.js</c>, <c>canBudgetByEdited.js</c>;
/// PLAN.md §7.1): the budget and its tasks as the inputs hold them, the live totals, the lock, and every change the
/// legacy page sends, in the legacy order and with the legacy texts:
/// <list type="bullet">
/// <item>a task change (type, quantity, unit price, add, remove) saves the new total at once
/// (<c>tasksTotal + trunc(tasksTotal · margin / 100)</c>); a task is saved on blur;</item>
/// <item>margin, total, days, validity, payment method and description are saved on blur (margin: then the total
/// the margin change computed); any save resets the complete/PDF flags on the server;</item>
/// <item>the budget is locked when complete and its PDF downloaded, re-evaluated whenever these flags change, and can
/// be toggled by hand; every change while locked shows "Oçamento bloqueado" (the payment method: "Não pode editar o
/// orçamento") and nothing is sent.</item>
/// </list>
/// Fixed legacy UI defects: the header's totals always use the rows' current values (legacy re-reads the saved tasks
/// whenever the total changes and then sums values the user has not saved yet with stale ones); after an import the
/// budget and its tasks are read again (D13; legacy shows <c>total + 1</c> and the old margin until a reload); the
/// saves are sent one after the other (legacy sends them concurrently, so an older answer can win); a budget without
/// tasks offers "Adicionar" (legacy renders nothing, so such a budget cannot get a task back).
/// </summary>
public sealed class BudgetDetailsState(IBudgetApi budgets, ITaskApi tasks, IPricesApi prices, IPaymentMethodsApi paymentMethods, ISnackbar snackbar)
{
    public const string LockedWarning = "Oçamento bloqueado";
    public const string PaymentLockedWarning = "Não pode editar o orçamento";
    public const string CompletedMessage = "Orçamento completo";
    public const string ImportedMessage = "Importado com sucesso";

    /// <summary>The saves run one after the other (the browser is single-threaded: no lock needed for the chain).</summary>
    private Task _writes = Task.CompletedTask;
    private readonly List<BudgetTaskRow> _tasks = [];
    private PricesResponse? _prices;

    /// <summary>Raised after every change of what the page shows.</summary>
    public event Action? Changed;

    public BudgetResponse? Budget { get; private set; }

    /// <summary>The 4xx text when the budget could not be read.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Everything the legacy page waits for has arrived (budget, tasks, types, prices, payment methods).</summary>
    public bool IsLoaded { get; private set; }

    public IReadOnlyList<BudgetTaskRow> Tasks => _tasks;

    public IReadOnlyList<string> TaskTypes { get; private set; } = [];

    public IReadOnlyList<string> PaymentMethods { get; private set; } = [];

    /// <summary>Legacy <c>canBudgetBeEdit</c>.</summary>
    public bool CanEdit { get; private set; }

    /// <summary>Tasks (<c>true</c>) or the description ("Alternar").</summary>
    public bool ShowTasks { get; private set; } = true;

    public JsInput Margin { get; private set; }

    public JsInput Total { get; private set; }

    public JsInput Days { get; private set; }

    public JsInput Validity { get; private set; }

    public string? PaymentMethod { get; private set; }

    public string? Description { get; private set; }

    /// <summary>"Tarefas - X €".</summary>
    public string TasksTotalText => JsNumber.ToFixed(TasksTotal, 2);

    /// <summary>"C / Margem - X €".</summary>
    public string WithMarginText => BudgetCalculations.WithMargin(TasksTotal, Margin.Value);

    /// <summary>The margin the saved total really gives.</summary>
    public string EffectiveMarginText => BudgetCalculations.EffectiveMargin(Margin.Value, TasksTotal, Total.Value);

    public string EffectiveMarginColor => BudgetCalculations.MarginColor(EffectiveMarginText);

    private double TasksTotal => BudgetCalculations.TasksTotal(_tasks.Select(t => (t.Quantity.Value, t.UnitPrice.Value)));

    public async Task LoadAsync(string budgetId)
    {
        var budget = await budgets.GetAsync(budgetId);
        if (!budget.IsSuccess)
        {
            LoadError = budget.IsUnexpected ? null : budget.ErrorText;
            ShowError(budget);
            Notify();
            return;
        }

        var list = await tasks.ListAsync(budgetId);
        var types = await tasks.ListTypesAsync();
        var current = await prices.GetAsync();
        var methods = await paymentMethods.ListAsync();
        _tasks.Clear();
        _tasks.AddRange((list.Value ?? []).Select(t => new BudgetTaskRow(t)));
        TaskTypes = types.Value ?? [];
        _prices = current.Value;
        PaymentMethods = [.. (methods.Value ?? []).Select(m => m.Name ?? string.Empty)];
        SetBudget(budget.Value!, initial: true);
        IsLoaded = list.IsSuccess && types.IsSuccess && current.IsSuccess && methods.IsSuccess;
        Notify();
    }

    public void ToggleView()
    {
        ShowTasks = !ShowTasks;
        Notify();
    }

    public void ToggleLock()
    {
        CanEdit = !CanEdit;
        Notify();
    }

    /// <summary>
    /// A key that would change an input while the budget is locked (the inputs are read-only then): the legacy
    /// warning, as legacy shows it for every refused change.
    /// </summary>
    public void LockedKey(string key)
    {
        if (!CanEdit && (key.Length == 1 || key is "Backspace" or "Delete" or "Enter"))
        {
            snackbar.Add(LockedWarning, Severity.Warning);
        }
    }

    /// <summary>The edit dialog saved the budget (legacy <c>editBudget</c>).</summary>
    public void Replace(BudgetResponse saved)
    {
        SetBudget(saved);
        Notify();
    }

    // ---------------------------------------------------------------- header inputs (save on blur)
    public void MarginChanged(string? text)
    {
        if (Refused())
        {
            return;
        }

        Margin = JsInput.Typed(text);
        Total = JsInput.Typed(BudgetCalculations.WithMargin(TasksTotal, Margin.Value)); // calculateTotalWithPredefinedMargin(target.value)
        Notify();
    }

    public Task MarginBlurAsync() => CanEdit ? WriteAsync(async () =>
    {
        var margin = await budgets.PatchAsync(Budget!.Id, new PatchBudgetRequest(BudgetFields.Margin, Margin.Json));
        if (!margin.IsSuccess)
        {
            ShowError(margin);
            return;
        }

        var total = await budgets.PatchAsync(Budget!.Id, new PatchBudgetRequest(BudgetFields.Total, Total.Json));
        Apply(total);
    }) : Task.CompletedTask;

    public void TotalChanged(string? text) => Change(() => Total = JsInput.Typed(text));

    public Task TotalBlurAsync() => PatchOnBlurAsync(BudgetFields.Total, Total.Json);

    public void DaysChanged(string? text) => Change(() => Days = JsInput.Typed(text));

    public Task DaysBlurAsync() => PatchOnBlurAsync(BudgetFields.DaysNeededToDoTheWork, Days.Json);

    public void ValidityChanged(string? text) => Change(() => Validity = JsInput.Typed(text));

    public Task ValidityBlurAsync() => PatchOnBlurAsync(BudgetFields.Validity, Validity.Json);

    public void PaymentMethodChanged(string? value)
    {
        if (!CanEdit)
        {
            snackbar.Add(PaymentLockedWarning, Severity.Warning);
            Notify(); // the select shows the saved value again
            return;
        }

        PaymentMethod = value;
        Notify();
    }

    public Task PaymentMethodBlurAsync() => PatchOnBlurAsync(BudgetFields.PaymentMethod, PaymentMethod);

    public void DescriptionChanged(string? text) => Change(() => Description = text);

    public Task DescriptionBlurAsync() => PatchOnBlurAsync(BudgetFields.Description, Description);

    /// <summary>"Concluir": <c>complete = true</c>; the page keeps its PDF flag (legacy merges only <c>complete</c>).</summary>
    public Task CompleteAsync()
    {
        if (Refused())
        {
            return Task.CompletedTask;
        }

        return WriteAsync(async () =>
        {
            var completed = await budgets.PatchAsync(Budget!.Id, new PatchBudgetRequest(BudgetFields.Complete, true));
            if (!completed.IsSuccess)
            {
                ShowError(completed);
                return;
            }

            SetFlags(completed.Value!.Complete, Budget.WasPdfDownloaded);
            snackbar.Add(CompletedMessage, Severity.Success);
        });
    }

    // ---------------------------------------------------------------- tasks
    /// <summary>A new type sets the type's price (prices of <c>others</c> and <c>components</c>: 1, as legacy <c>usePrices</c>).</summary>
    public Task TaskTypeChangedAsync(BudgetTaskRow row, string? type)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Refused())
        {
            return Task.CompletedTask;
        }

        row.TaskType = type;
        row.UnitPrice = JsInput.Of(Price(type));
        return SaveTotalAsync();
    }

    public Task QuantityChangedAsync(BudgetTaskRow row, string? text)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Refused())
        {
            return Task.CompletedTask;
        }

        row.Quantity = JsInput.Typed(text);
        return SaveTotalAsync();
    }

    public Task UnitPriceChangedAsync(BudgetTaskRow row, string? text)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Refused())
        {
            return Task.CompletedTask;
        }

        row.UnitPrice = JsInput.Typed(text);
        return SaveTotalAsync();
    }

    public void TaskDescriptionChanged(BudgetTaskRow row, string? text)
    {
        ArgumentNullException.ThrowIfNull(row);
        Change(() => row.Description = text);
    }

    /// <summary>Legacy <c>submitForm</c> (blur of any of the row's inputs): save the task; the page marks the budget incomplete.</summary>
    public Task TaskBlurAsync(BudgetTaskRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return CanEdit ? WriteAsync(async () =>
        {
            var saved = await tasks.UpdateAsync(row.Id, row.BudgetId, row.ToUpdateRequest());
            if (!saved.IsSuccess)
            {
                ShowError(saved);
                return;
            }

            row.Load(saved.Value!);
            SetFlags(false, Budget!.WasPdfDownloaded);
        }) : Task.CompletedTask;
    }

    /// <summary>"Adicionar": a default task for the row's budget, then the new total.</summary>
    public Task AddTaskAsync(BudgetTaskRow? row)
    {
        if (Refused())
        {
            return Task.CompletedTask;
        }

        return WriteAsync(async () =>
        {
            var created = await tasks.CreateDefaultAsync(row?.BudgetId ?? Budget!.Id);
            if (!created.IsSuccess)
            {
                ShowError(created);
                return;
            }

            _tasks.Add(new BudgetTaskRow(created.Value!));
            await PatchTotalFromTasksAsync();
        });
    }

    /// <summary>"Apagar" (offered while there is more than one task), then the new total.</summary>
    public Task RemoveTaskAsync(BudgetTaskRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Refused())
        {
            return Task.CompletedTask;
        }

        return WriteAsync(async () =>
        {
            var deleted = await tasks.DeleteAsync(row.Id, row.BudgetId);
            if (!deleted.IsSuccess)
            {
                ShowError(deleted);
                return;
            }

            _tasks.RemoveAll(t => t.Id == deleted.Value!.Id);
            await PatchTotalFromTasksAsync();
        });
    }

    // ---------------------------------------------------------------- import
    /// <summary>The import dialog's "Importar": <c>true</c> when it worked (the dialog closes).</summary>
    public async Task<bool> ImportAsync(ImportBudgetRequest request)
    {
        var ok = false;
        await WriteAsync(async () =>
        {
            var imported = await budgets.ImportAsync(Budget!.Id, request);
            if (!imported.IsSuccess)
            {
                ShowError(imported);
                return;
            }

            // D13: read the budget and its tasks again (legacy sets total + 1 to trigger a reload of the tasks).
            var list = await tasks.ListAsync(Budget.Id);
            _tasks.Clear();
            _tasks.AddRange((list.Value ?? []).Select(t => new BudgetTaskRow(t)));
            SetBudget(imported.Value!);
            snackbar.Add(ImportedMessage, Severity.Success);
            ok = true;
        });
        return ok;
    }

    // ---------------------------------------------------------------- helpers
    private double? Price(string? type)
    {
        if (type is "others" or "components")
        {
            return 1;
        }

        return type switch
        {
            "automation" => _prices?.Automation,
            "consultancy" => _prices?.Consultancy,
            "development" => _prices?.Development,
            "machining" => _prices?.Machining,
            "assembly" => _prices?.Assembly,
            _ => null,
        };
    }

    /// <summary>Legacy <c>updateBudgetTotal</c> after a task change, before the task itself is saved (on blur).</summary>
    private Task SaveTotalAsync()
    {
        Notify();
        return WriteAsync(PatchTotalFromTasksAsync);
    }

    private async Task PatchTotalFromTasksAsync()
    {
        var total = BudgetCalculations.TotalFromTasks(TasksTotal, Margin.Value);
        var saved = await budgets.PatchAsync(Budget!.Id, new PatchBudgetRequest(BudgetFields.Total, double.IsFinite(total) ? total : null));
        Apply(saved);
    }

    private Task PatchOnBlurAsync(string field, object? value) =>
        CanEdit ? WriteAsync(async () => Apply(await budgets.PatchAsync(Budget!.Id, new PatchBudgetRequest(field, value)))) : Task.CompletedTask;

    private void Apply(ApiResult<BudgetResponse> saved)
    {
        if (saved.IsSuccess)
        {
            SetBudget(saved.Value!);
        }
        else
        {
            ShowError(saved);
        }
    }

    /// <summary>A local change, or the locked warning.</summary>
    private void Change(Action change)
    {
        if (Refused())
        {
            return;
        }

        change();
        Notify();
    }

    private bool Refused()
    {
        if (CanEdit)
        {
            return false;
        }

        snackbar.Add(LockedWarning, Severity.Warning);
        Notify();
        return true;
    }

    /// <summary>Legacy <c>setBudget(saved)</c>: the inputs show the saved values; the lock follows the flags when they change.</summary>
    private void SetBudget(BudgetResponse saved, bool initial = false)
    {
        var flagsChanged = initial || Budget is null || Budget.Complete != saved.Complete || Budget.WasPdfDownloaded != saved.WasPdfDownloaded;
        Budget = saved;
        Margin = JsInput.Of(saved.Margin);
        Total = JsInput.Of(saved.Total);
        Days = JsInput.Of(saved.DaysNeededToDoTheWork);
        Validity = JsInput.Of(saved.Validity);
        PaymentMethod = saved.PaymentMethod;
        Description = saved.Description;
        if (flagsChanged)
        {
            CanEdit = !(saved.Complete && saved.WasPdfDownloaded);
        }
    }

    /// <summary>Legacy <c>setBudget({ ...budget, complete })</c>: only the flags; the inputs keep what they hold.</summary>
    private void SetFlags(bool complete, bool wasPdfDownloaded)
    {
        var changed = Budget!.Complete != complete || Budget.WasPdfDownloaded != wasPdfDownloaded;
        Budget = Budget with { Complete = complete, WasPdfDownloaded = wasPdfDownloaded };
        if (changed)
        {
            CanEdit = !(complete && wasPdfDownloaded);
        }
    }

    private Task WriteAsync(Func<Task> write)
    {
        _writes = RunAfterAsync(_writes, write);
        return _writes;
    }

    private async Task RunAfterAsync(Task previous, Func<Task> write)
    {
        try
        {
            await previous;
        }
        catch (HttpRequestException)
        {
            // already shown by the global error notification; the next save still runs
        }

        try
        {
            await write();
        }
        finally
        {
            Notify();
        }
    }

    private void ShowError<T>(ApiResult<T> result)
    {
        if (!result.IsUnexpected && result.ErrorText is { } text)
        {
            snackbar.Add(text, Severity.Error);
        }
    }

    private void Notify() => Changed?.Invoke();
}

/// <summary>The budget fields the details page saves with <c>patchProperty</c> (legacy <c>patchBudgetPropertyNames.js</c>).</summary>
public static class BudgetFields
{
    public const string DaysNeededToDoTheWork = "daysNeededToDoTheWork";
    public const string Total = "total";
    public const string PaymentMethod = "paymentMethod";
    public const string Margin = "margin";
    public const string Description = "description";
    public const string Complete = "complete";
    public const string Validity = "validity";
}
