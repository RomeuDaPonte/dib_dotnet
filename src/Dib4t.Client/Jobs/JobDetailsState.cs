using Dib4t.Client.Api;
using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Calculations;
using Dib4t.SharedKernel.Vocabulary;
using MudBlazor;

namespace Dib4t.Client.Jobs;

/// <summary>
/// The job details page (legacy <c>jobDetais/*.jsx</c>, <c>jobContext.js</c>, <c>useJobResultsContext.js</c>; PLAN.md
/// §7.3): the job with its budget, the budget's tasks and the hourly prices; the header form (delivery date, billed
/// %, order identification, official flag), the observations, the state end dates, the cost table's registered
/// values and the verifications, with the legacy requests, toasts and texts. The cost summary and rows are
/// <see cref="JobCostCalculations"/> of the values as the inputs hold them (typed texts included), so they change
/// while typing, as legacy.
/// Fixed legacy UI defects: a field is sent when its value changed (legacy sends on every blur, also unchanged, with a
/// success toast); the official flag is sent when ticked (legacy: on leaving the checkbox); the dates are shown in the
/// browser's zone (legacy: the UTC date, the day before for a time stamped between 00:00 and 01:00 in Lisbon's summer);
/// a state date the server cleared shows empty (legacy keeps the old one); the verifications send the job's own
/// machining statistic with an assembly check (legacy sends the assembly one twice; the server uses only one); the
/// page of a job without statistics opens (legacy crashes).
/// </summary>
public sealed class JobDetailsState(IJobApi jobs, ITaskApi taskApi, IPricesApi pricesApi, ISnackbar snackbar)
{
    public const string UpdatedMessage = "Obra atualizada com sucesso!";
    public const string DeliveryDateWarning = "Não pode alterar a data de entrega com a obra no corrente estado";
    public const string RegisteredMessage = "Registo atualizado";
    public const string DateUpdatedMessage = "Data atualizada";
    public const string VerificationsUpdatedMessage = "Verificações atualizadas";
    public const string FailedMessage = "Algo deu errado";

    /// <summary>The <c>taskType</c> of each registered value (<c>PATCH register</c>), in the table's order.</summary>
    public static IReadOnlyList<string> RegisterTypes { get; } =
    [
        TaskTypes.Development, TaskTypes.Components, TaskTypes.Automation, TaskTypes.Assembly, TaskTypes.Machining, "materials", TaskTypes.Others,
        "custoNaoQualidade",
    ];

    /// <summary>The editable state end dates (legacy <c>jobStateChangeDetails.jsx</c>), with their labels.</summary>
    public static IReadOnlyList<(string Field, string Label, string State)> StateDates { get; } =
    [
        ("devEndedInDate", "Desenvolvimento", JobStates.Development),
        ("onApprovalEndedInDate", "Em aprovação", JobStates.OnApproval),
        ("approvedEndedInDate", "Aprovado", JobStates.Approved),
        ("machiningEndedInDate", "Maquinação", JobStates.Machining),
        ("assemblyEndedInDate", "Montagem", JobStates.Assembly),
    ];

    private readonly Dictionary<string, JsValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsValue> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dates = new(StringComparer.Ordinal);
    private Task _writes = Task.CompletedTask;
    private IReadOnlyList<CostTask> _tasks = [];
    private Dictionary<string, double> _prices = new(StringComparer.Ordinal);

    /// <summary>Raised after every change of what the page shows.</summary>
    public event Action? Changed;

    public JobResponse<BudgetResponse>? Job { get; private set; }

    /// <summary>The job could not be read (unknown id: legacy shows an empty page).</summary>
    public bool NotFound { get; private set; }

    /// <summary>The tasks and prices have arrived (legacy computes with them as they come).</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The browser's zone for the dates (tests set it).</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public string DeliveryDate { get; private set; } = string.Empty;

    public string OrderIdentification { get; private set; } = string.Empty;

    public bool IsOfficialOrderIdentification { get; private set; }

    /// <summary>The billed % input (legacy shows <c>form.invoicedPercent || 0</c>).</summary>
    public JsValue InvoicedPercent { get; private set; }

    public string Comments { get; private set; } = string.Empty;

    /// <summary>The cost summary and rows for the current inputs.</summary>
    public JobCostPage Costs => JobCostCalculations.Page(_tasks, new JobCostValues(
        Hours(Job?.DevStatistic is null, TaskTypes.Development), Hours(Job?.MachiningStatistic is null, TaskTypes.Machining),
        Hours(Job?.AssemblyStatistic is null, TaskTypes.Assembly), Value(TaskTypes.Components), Value(TaskTypes.Automation), Value("materials"),
        Value(TaskTypes.Others), Value("custoNaoQualidade")), Job?.Budget?.Total ?? double.NaN, _prices);

    /// <summary>What a registered value's input shows (<c>null</c> and missing: empty).</summary>
    public string Input(string type) => Display(_values.GetValueOrDefault(type));

    /// <summary>The billed % input: legacy <c>form.invoicedPercent || 0</c>.</summary>
    public string InvoicedPercentText => InvoicedPercent.IsTruthy ? InvoicedPercent.ToString() : "0";

    /// <summary>A state end date's input (<c>yyyy-MM-dd</c> in the browser's zone, or empty).</summary>
    public string StateDate(string field) => _dates.GetValueOrDefault(field, string.Empty);

    /// <summary>A date of the job shown read-only (budget date, start date, billing end) in the browser's zone.</summary>
    public string DateText(DateTime? utc) => LocalDate(utc);

    public async Task LoadAsync(string jobId)
    {
        var job = await jobs.GetByIdAsync(jobId);
        if (!job.IsSuccess || job.Value is null)
        {
            NotFound = true;
            Notify();
            return;
        }

        var budgetId = job.Value.Budget?.Id;
        var list = budgetId is null ? ApiResult.Success<IReadOnlyList<BudgetTaskResponse>>([]) : await taskApi.ListAsync(budgetId);
        var prices = await pricesApi.GetAsync();
        _tasks = [.. (list.Value ?? []).Select(t => new CostTask(t.TaskType, t.Quantity ?? double.NaN, t.UnitPrice ?? double.NaN))];
        _prices = Prices(prices.Value);
        Replace(job.Value);
        IsLoaded = list.IsSuccess && prices.IsSuccess;
        Notify();
    }

    // ------------------------------------------------------------ header and observations (PATCH singleProperty)

    public void DeliveryDateChanged(string? value)
    {
        DeliveryDate = value ?? string.Empty;
        Notify();
    }

    public void OrderIdentificationChanged(string? value)
    {
        OrderIdentification = value ?? string.Empty;
        Notify();
    }

    public void InvoicedPercentChanged(string? value)
    {
        InvoicedPercent = JsValue.Text(value ?? string.Empty);
        Notify();
    }

    public void CommentsChanged(string? value)
    {
        Comments = value ?? string.Empty;
        Notify();
    }

    /// <summary>Legacy <c>handleBlur</c> of the delivery date: only in "Montagem" or "Para faturação" (else the warning, nothing sent).</summary>
    public Task DeliveryDateBlurAsync()
    {
        if (Job is not { } job || DeliveryDate == LocalDate(job.DeliveryDate.HasValue ? job.DeliveryDate.Value : null))
        {
            return Task.CompletedTask;
        }

        if (job.State is not (JobStates.Assembly or JobStates.ForInvoicing))
        {
            snackbar.Add(DeliveryDateWarning, Severity.Warning);
            return Task.CompletedTask;
        }

        return SendPropertyAsync("deliveryDate", DeliveryDate);
    }

    public Task OrderIdentificationBlurAsync() =>
        Job is { } job && OrderIdentification != job.OrderIdentification ? SendPropertyAsync("orderIdentification", OrderIdentification) : Task.CompletedTask;

    public Task OfficialChangedAsync(bool value)
    {
        IsOfficialOrderIdentification = value;
        Notify();
        return SendPropertyAsync("isOfficialOrderIdentification", value);
    }

    public Task InvoicedPercentBlurAsync() =>
        Job is { } job && Display(InvoicedPercent) != Display(Of(job.InvoicedPercent)) ? SendPropertyAsync("invoicedPercent", Json(InvoicedPercent)) : Task.CompletedTask;

    public Task CommentsBlurAsync() =>
        Job is { } job && Comments != (job.Comments.HasValue ? job.Comments.Value : string.Empty)
            ? SendPropertyAsync("comments", Comments)
            : Task.CompletedTask;

    // ------------------------------------------------------------ state end dates (PATCH statisticsDates)

    public void StateDateChanged(string field, string? value)
    {
        _dates[field] = value ?? string.Empty;
        Notify();
    }

    public Task StateDateBlurAsync(string field)
    {
        if (Job is not { } job || StateDate(field) == SavedDate(job, field))
        {
            return Task.CompletedTask;
        }

        var value = StateDate(field);
        return EnqueueAsync(async () =>
        {
            var saved = await jobs.ChangeStatisticsDateAsync(job.Id, field, value);
            Answer(saved, DateUpdatedMessage, FailedMessage);
        });
    }

    // ------------------------------------------------------------ registered values (PATCH register)

    public void ValueChanged(string type, string? text)
    {
        _values[type] = JsValue.Text(text ?? string.Empty);
        Notify();
    }

    /// <summary>Legacy <c>handleBlur</c> of a row: <c>patchRegisteredValue(value, type)</c>, "Registo atualizado" (the answer is not shown).</summary>
    public Task ValueBlurAsync(string type)
    {
        if (Job is not { } job || !_values.TryGetValue(type, out var value) || Display(value) == Display(_saved.GetValueOrDefault(type)))
        {
            return Task.CompletedTask;
        }

        return EnqueueAsync(async () =>
        {
            var saved = await jobs.RegisterValueAsync(job.Id, Json(value), type);
            if (saved.IsSuccess)
            {
                _saved[type] = value;
                snackbar.Add(RegisteredMessage, Severity.Success);
            }
            else if (!saved.IsUnexpected)
            {
                snackbar.Add(saved.ErrorText!, Severity.Error);
            }
        });
    }

    // ------------------------------------------------------------ verifications (PATCH updateVerifications)

    /// <summary>
    /// A check's answer (Ok, Nok, n/a) or the tries of the first test: the job's statistic with the answer (and the
    /// hours as the row holds them, as legacy sends its context's job), the other statistic as read.
    /// </summary>
    public Task VerificationChangedAsync(string property, string answer)
    {
        if (Job is not { } job)
        {
            return Task.CompletedTask;
        }

        var machining = MachiningChecks.Contains(property, StringComparer.Ordinal);
        var machiningStatistic = Statistic(job.MachiningStatistic, TaskTypes.Machining);
        var assemblyStatistic = Statistic(job.AssemblyStatistic, TaskTypes.Assembly);
        (machining ? machiningStatistic : assemblyStatistic)[property] = answer;
        var body = new Dictionary<string, object?>
        {
            ["machiningStatistic"] = machiningStatistic,
            ["assemblyStatistic"] = assemblyStatistic,
            ["updateMachiningVerificatons"] = machining,
        };
        return EnqueueAsync(async () =>
        {
            var saved = await jobs.UpdateVerificationsAsync(job.Id, body);
            Answer(saved, VerificationsUpdatedMessage, FailedMessage);
        });
    }

    private static readonly string[] MachiningChecks = ["verificationAndShapeControl", "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish"];

    /// <summary>The statistic as the page holds it (<c>{ ...job.xStatistic }</c>), <c>null</c>/missing values left out.</summary>
    private Dictionary<string, object?> Statistic(object? statistic, string hoursType)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (statistic)
        {
            case JobMachiningStatisticResponse m:
                fields["budgetedHours"] = m.BudgetedHours;
                fields["deviation"] = m.Deviation;
                fields["verificationAndShapeControl"] = m.VerificationAndShapeControl;
                fields["dimensionalVerificationAndControl"] = m.DimensionalVerificationAndControl;
                fields["analysisOfTheDegreeOfFinish"] = m.AnalysisOfTheDegreeOfFinish;
                fields["_id"] = m.Id;
                break;
            case JobAssemblyStatisticResponse a:
                fields["budgetedHours"] = a.BudgetedHours;
                fields["deviation"] = a.Deviation;
                fields["pneumaticCircuits"] = a.PneumaticCircuits;
                fields["electricalBoardsAndCircuits"] = a.ElectricalBoardsAndCircuits;
                fields["kinematics"] = a.Kinematics;
                fields["programmingFeaturesPLC"] = a.ProgrammingFeaturesPlc;
                fields["operator_equipmentSafetyConditions"] = a.OperatorEquipmentSafetyConditions;
                fields["visualInspection"] = a.VisualInspection;
                fields["validTryOnFirstTest"] = a.ValidTryOnFirstTest;
                fields["_id"] = a.Id;
                break;
            default:
                return fields; // no statistic yet: the server creates it from the answer alone
        }

        if (_values.TryGetValue(hoursType, out var hours) && Json(hours) is { } sent)
        {
            fields["numberOfHoursUsed"] = sent;
        }

        return fields;
    }

    // ------------------------------------------------------------ plumbing

    private Task SendPropertyAsync(string name, object? value)
    {
        var job = Job!;
        return EnqueueAsync(async () =>
        {
            var saved = await jobs.ChangeSinglePropertyAsync(job.Id, name, value);
            Answer(saved, UpdatedMessage, null);
        });
    }

    /// <summary>The job the server answered (legacy <c>setJob(updatedJob)</c>) and the toast; the error text, or <paramref name="failure"/>.</summary>
    private void Answer(ApiResult<JobResponse<BudgetResponse>> saved, string success, string? failure)
    {
        if (saved.IsSuccess && saved.Value is { } job)
        {
            Replace(job);
            snackbar.Add(success, Severity.Success);
        }
        else if (!saved.IsUnexpected)
        {
            snackbar.Add(failure ?? saved.ErrorText!, Severity.Error);
            if (Job is { } current)
            {
                Replace(current); // the inputs show the job again
            }
        }

        Notify();
    }

    /// <summary>The saves run one after the other, in the order the user made them.</summary>
    private Task EnqueueAsync(Func<Task> write)
    {
        _writes = _writes.ContinueWith(_ => write(), TaskScheduler.Current).Unwrap();
        return _writes;
    }

    private void Replace(JobResponse<BudgetResponse> job)
    {
        Job = job;
        DeliveryDate = LocalDate(job.DeliveryDate.HasValue ? job.DeliveryDate.Value : null);
        OrderIdentification = job.OrderIdentification;
        IsOfficialOrderIdentification = job.IsOfficialOrderIdentification.OrDefault();
        InvoicedPercent = Of(job.InvoicedPercent);
        Comments = job.Comments.HasValue ? job.Comments.Value ?? string.Empty : string.Empty;
        foreach (var (field, _, _) in StateDates)
        {
            _dates[field] = SavedDate(job, field);
        }

        SetValue(TaskTypes.Development, job.DevStatistic is { } dev ? Of(dev.NumberOfHoursUsed) : JsValue.Undefined);
        SetValue(TaskTypes.Machining, job.MachiningStatistic is { } m ? Of(m.NumberOfHoursUsed) : JsValue.Undefined);
        SetValue(TaskTypes.Assembly, job.AssemblyStatistic is { } a ? Of(a.NumberOfHoursUsed) : JsValue.Undefined);
        SetValue(TaskTypes.Components, Of(job.Components));
        SetValue(TaskTypes.Automation, Of(job.Automation));
        SetValue("materials", Of(job.Materials));
        SetValue(TaskTypes.Others, Of(job.Others));
        SetValue("custoNaoQualidade", Of(job.CostNonQuality));
    }

    private void SetValue(string type, JsValue value)
    {
        _values[type] = value;
        _saved[type] = value;
    }

    private string SavedDate(JobResponse<BudgetResponse> job, string field)
    {
        var dates = job.JobStatisticsDates;
        var date = field switch
        {
            "devEndedInDate" => dates?.DevEndedInDate,
            "onApprovalEndedInDate" => dates?.OnApprovalEndedInDate,
            "approvedEndedInDate" => dates?.ApprovedEndedInDate,
            "machiningEndedInDate" => dates?.MachiningEndedInDate,
            "assemblyEndedInDate" => dates?.AssemblyEndedInDate,
            _ => dates?.ForInvoicingEndedInDate,
        };
        return LocalDate(date is { HasValue: true } value ? value.Value : null);
    }

    private string LocalDate(DateTime? utc) =>
        utc is { } value ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), Zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A missing statistic's hours count as 0 (fixed: legacy crashes); else the input's value.</summary>
    private JsValue Hours(bool missing, string type) => missing ? JsValue.Undefined : Value(type);

    private JsValue Value(string type) => _values.GetValueOrDefault(type);

    /// <summary>What an input shows of a value (<c>null</c> and missing: empty).</summary>
    private static string Display(JsValue value) => value.IsUndefined || value.IsNull ? string.Empty : value.ToString();

    private static JsValue Of(Stored<double> value) => value.HasValue ? JsValue.Of(value.Value) : value.IsNull ? JsValue.Null : JsValue.Undefined;

    /// <summary>What <c>JSON.stringify</c> sends of the value: the typed text, the number (<c>NaN</c> → <c>null</c>), <c>null</c>.</summary>
    private static object? Json(JsValue value) =>
        value.IsText ? value.ToString() : !value.IsUndefined && !value.IsNull && double.IsFinite(value.ToNumber()) ? value.ToNumber() : null;

    private static Dictionary<string, double> Prices(Contracts.Settings.PricesResponse? prices)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string type, double? value)
        {
            if (value is { } number)
            {
                result[type] = number;
            }
        }

        Add(TaskTypes.Automation, prices?.Automation);
        Add(TaskTypes.Consultancy, prices?.Consultancy);
        Add(TaskTypes.Development, prices?.Development);
        Add(TaskTypes.Machining, prices?.Machining);
        Add(TaskTypes.Assembly, prices?.Assembly);
        return result;
    }

    private void Notify() => Changed?.Invoke();
}
