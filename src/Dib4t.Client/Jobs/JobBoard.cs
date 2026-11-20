using System.Globalization;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Jobs;

/// <summary>
/// The jobs board's client-side rules (legacy <c>components/job/*</c>, PLAN.md §7.3 "Jobs UI"): the tabs, the first
/// tab per field of competence, the rows' icons, the due-date countdown, the copy path, the menus and how a saved job
/// changes the list.
/// </summary>
public static class JobBoard
{
    /// <summary>The tabs in the legacy order (<c>job.jsx</c>): label, icon, the states listed, the counter shown.</summary>
    public static IReadOnlyList<JobTab> Tabs { get; } =
    [
        new("Todas", JobIcons.All, null, c => c.AllCount),
        new("Desenvolvimento", JobIcons.Development, [JobStates.Development], c => c.DevCount),
        new("Em aprovação", JobIcons.OnApproval, [JobStates.OnApproval], c => c.OnApprovalCount),
        new("Aprovadas", JobIcons.Approved, [JobStates.Approved], c => c.ApprovedCount),
        new("Maquinação", JobIcons.Machining, [JobStates.Machining], c => c.MachiningCount),
        new("Montagem", JobIcons.Assembly, [JobStates.Assembly], c => c.AssemblyCount),
        new("Para faturação", JobIcons.ForInvoicing, [JobStates.ForInvoicing], c => c.ForInvoicingCount),
        new("Faturadas", JobIcons.Billed, [JobStates.Billed, JobStates.Deleted], null),
    ];

    /// <summary>The "Para faturação" tab (its rows show "Ft %").</summary>
    public const int ForInvoicingTab = 6;

    /// <summary>Legacy <c>displayTab</c>: the user's field of competence picks the first tab (else "Todas").</summary>
    public static int InitialTab(string? fieldOfCompetencies) => fieldOfCompetencies switch
    {
        JobStates.Development => 1,
        JobStates.Machining => 4,
        JobStates.Assembly => 5,
        JobStates.ForInvoicing => 6,
        _ => 0,
    };

    /// <summary>
    /// The "Faturadas" tab (legacy <c>useJobs</c>): the billed jobs, then the deleted ones, by due date (a stable sort:
    /// legacy's comparator never answers 0, which leaves equal dates in an engine-dependent order).
    /// </summary>
    public static List<JobRow> Merged(IEnumerable<JobRow> billed, IEnumerable<JobRow> deleted) =>
        [.. billed.Concat(deleted).OrderBy(r => r.DueDate ?? DateTime.MinValue)];

    /// <summary>Legacy <c>showWarningIfNotOk</c>: any check "Nok", or the first test not valid on the first try.</summary>
    public static bool HasNonConformity(JobMachiningStatisticResponse? machining, JobAssemblyStatisticResponse? assembly) =>
        (machining is { } m && new[] { m.AnalysisOfTheDegreeOfFinish, m.DimensionalVerificationAndControl, m.VerificationAndShapeControl }.Contains(QuestionResponses.NotOk))
        || (assembly is { } a && (new[]
        {
            a.ElectricalBoardsAndCircuits, a.Kinematics, a.OperatorEquipmentSafetyConditions, a.PneumaticCircuits, a.ProgrammingFeaturesPlc, a.VisualInspection,
        }.Contains(QuestionResponses.NotOk) || a.ValidTryOnFirstTest != QuestionResponses.DefaultFirstTestTry));

    /// <summary>The priority icon and the priority/print menu: not for jobs being billed, billed or deleted.</summary>
    public static bool ShowsPriority(string? state) => state is not (JobStates.ForInvoicing or JobStates.Billed or JobStates.Deleted);

    /// <summary>The countdown is hidden (kept in place, transparent) for jobs being billed, billed or deleted.</summary>
    public static bool HidesCountdown(string? state) => !ShowsPriority(state);

    /// <summary>
    /// Days left until the due date (legacy <c>calculateDefirenceInDays</c>): the browser's local dates of today and
    /// of the due date; −1 once it has passed. Counted in calendar days (legacy divides the milliseconds between the
    /// two local midnights and rounds up, so the day the clocks go back counts twice).
    /// </summary>
    public static int DaysLeft(DateTime dueDateUtc, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var due = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dueDateUtc, DateTimeKind.Utc), zone));
        var days = due.DayNumber - today.DayNumber;
        return days < 0 ? -1 : days;
    }

    /// <summary>The countdown icon: red "running with errors" at 0 days or late, yellow 1–4, green 5–9, "9+" beyond.</summary>
    public static (string Icon, string Color) Countdown(int daysLeft) => daysLeft switch
    {
        <= 0 => (JobIcons.Late, "job-countdown-red"),
        <= 4 => (JobIcons.Digit(daysLeft), "job-countdown-yellow"),
        <= 9 => (JobIcons.Digit(daysLeft), "job-countdown-green"),
        _ => (JobIcons.NinePlus, "job-countdown-green"),
    };

    /// <summary>The date input's value: legacy <c>toISOString().substr(0, 10)</c>, the UTC date.</summary>
    public static string DateInput(DateTime? utc) =>
        utc is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Legacy <c>toLocaleDateString("pt-pt")</c>: <c>dd/MM/yyyy</c> in the browser's zone.</summary>
    public static string Date(DateTime? utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return utc is { } value
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : "Invalid Date";
    }

    /// <summary>
    /// Legacy <c>copyToClipboardClicked</c>: <c>D:\NAS\Obras\20{first two digits of the number}\{client}\{number} -
    /// {descriptive}</c>, plus <c>\CAD</c> in development, approval and approved, <c>\CAM</c> in machining.
    /// </summary>
    public static string CopyPath(string? budgetNumber, string? client, string? descriptive, string? state)
    {
        var year = "20" + (budgetNumber is { Length: >= 2 } number ? number[..2] : budgetNumber ?? string.Empty);
        var root = $"D:\\NAS\\Obras\\{year}\\{client}\\{budgetNumber} - {descriptive}";
        return state switch
        {
            JobStates.Development or JobStates.OnApproval or JobStates.Approved => root + "\\CAD",
            JobStates.Machining => root + "\\CAM",
            _ => root,
        };
    }

    /// <summary>The state menu (legacy <c>jobOptions.jsx</c>): the six states, then "Faturada" and "Eliminar" only from "Para faturação".</summary>
    public static IReadOnlyList<(string State, string Label, string Icon)> StateMenu(string? currentState)
    {
        var items = new List<(string, string, string)>
        {
            (JobStates.Development, JobStates.Development, JobIcons.Development),
            (JobStates.OnApproval, JobStates.OnApproval, JobIcons.OnApproval),
            (JobStates.Approved, JobStates.Approved, JobIcons.Approved),
            (JobStates.Machining, JobStates.Machining, JobIcons.Machining),
            (JobStates.Assembly, JobStates.Assembly, JobIcons.Assembly),
            (JobStates.ForInvoicing, JobStates.ForInvoicing, JobIcons.ForInvoicing),
        };
        if (currentState == JobStates.ForInvoicing)
        {
            items.Add((JobStates.Billed, JobStates.Billed, JobIcons.Billed));
            items.Add((JobStates.Deleted, "Eliminar", JobIcons.Delete));
        }

        return items;
    }

    /// <summary>
    /// What a state choice does (legacy <c>updateState</c> + <c>changeStateimmediately</c> + <c>useJobStatisticsModalState</c>):
    /// "Eliminar", or any choice on a billed or deleted job, changes the state at once (<c>PATCH state</c>); from the
    /// six states before billing it opens the statistics dialog.
    /// </summary>
    public static StateChoice Choose(string? currentState, string newState) =>
        newState == JobStates.Deleted || currentState is JobStates.Billed or JobStates.Deleted
            ? StateChoice.ChangeNow
            : JobStatisticsForm.HasForm(currentState) ? StateChoice.OpenStatistics : StateChoice.Nothing;

    /// <summary>Legacy <c>updateJobs</c>: the saved job replaces its row.</summary>
    public static List<JobRow> Replaced(IEnumerable<JobRow> rows, JobRow saved) => [.. rows.Select(r => r.Id == saved.Id ? saved : r)];

    /// <summary>Legacy <c>removeFromList</c>: a tab of one state drops the job once its state changed.</summary>
    public static List<JobRow> WithoutMoved(IEnumerable<JobRow> rows, JobRow saved) =>
        [.. rows.Where(r => r.Id != saved.Id || r.State == saved.State).Select(r => r.Id == saved.Id ? saved : r)];

    /// <summary>Legacy <c>orderJobs</c> (after a priority or due-date change): the row replaced, by due date, then by priority.</summary>
    public static List<JobRow> Reordered(IEnumerable<JobRow> rows, JobRow saved)
    {
        var byDate = Replaced(rows, saved).OrderBy(r => r.DueDate ?? DateTime.MinValue).ToList();
        return
        [
            .. byDate.Where(r => r.Priority == JobPriorities.High),
            .. byDate.Where(r => r.Priority == JobPriorities.Medium),
            .. byDate.Where(r => r.Priority is not (JobPriorities.High or JobPriorities.Medium)),
        ];
    }

    /// <summary>
    /// The priority menu (legacy <c>jobExtraOptions.jsx</c>): label, priority, icon; then (after the legacy empty item)
    /// "Imprimir", which opens <c>PrintLabelsDialog</c> for the row's budget.
    /// </summary>
    public static IReadOnlyList<(string Label, string Priority, string Icon)> PriorityMenu { get; } =
    [
        ("Prioritária", JobPriorities.High, JobIcons.PriorityHigh),
        ("Intermédia", JobPriorities.Medium, JobIcons.PriorityMedium),
        ("Não prioritária", JobPriorities.Low, JobIcons.PriorityLow),
    ];
}

/// <summary>A board tab.</summary>
public sealed record JobTab(string Label, string Icon, IReadOnlyList<string>? States, Func<JobCountsResponse, int>? Count);

/// <summary>What choosing a state in the menu does.</summary>
public enum StateChoice
{
    Nothing,
    ChangeNow,
    OpenStatistics,
}

/// <summary>
/// One board row (legacy <c>jobsTable.jsx</c>'s row object, from the list or from a saved job): what the columns,
/// icons and menus need.
/// </summary>
public sealed record JobRow(
    string Id,
    string? BudgetId,
    string? BudgetNumber,
    double? Revision,
    string? Client,
    string? Descriptive,
    DateTime? StartDate,
    DateTime? DueDate,
    string State,
    bool IsOfficialOrderIdentification,
    bool HasNonConformity,
    string? Priority,
    bool IsDelivered,
    double? InvoicedPercent,
    string? Comments)
{
    /// <summary>A row of a list answer.</summary>
    public static JobRow From(JobResponse<JobListBudgetResponse> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id, job.Budget?.Id, job.Budget?.BudgetNumber, job.Budget?.Revision, job.Budget?.Client?.Name, job.Budget?.Descriptive,
            job.StartDate, job.DueDate, job.State, job.IsOfficialOrderIdentification.OrDefault(), JobBoard.HasNonConformity(job.MachiningStatistic, job.AssemblyStatistic),
            job.Priority, job.DeliveryDate.HasValue, job.InvoicedPercent.HasValue ? job.InvoicedPercent.Value : null, job.Comments.OrDefault());
    }

    /// <summary>A row of a saved job's answer (budget, client and author populated).</summary>
    public static JobRow From(JobResponse<Contracts.Budgets.BudgetResponse> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id, job.Budget?.Id, job.Budget?.BudgetNumber, job.Budget?.Revision, job.Budget?.Client?.Name, job.Budget?.Descriptive,
            job.StartDate, job.DueDate, job.State, job.IsOfficialOrderIdentification.OrDefault(), JobBoard.HasNonConformity(job.MachiningStatistic, job.AssemblyStatistic),
            job.Priority, job.DeliveryDate.HasValue, job.InvoicedPercent.HasValue ? job.InvoicedPercent.Value : null, job.Comments.OrDefault());
    }

    /// <summary>"Emcomenda" (sic, legacy tooltip): the official order of a job being billed.</summary>
    public bool ShowsOrderCheck => State == JobStates.ForInvoicing && IsOfficialOrderIdentification;

    /// <summary>"Entregue": a job being billed with a delivery date.</summary>
    public bool ShowsDelivered => State == JobStates.ForInvoicing && IsDelivered;
}

/// <summary>The legacy <c>@mui/icons-material</c> icons of the board (same Material icon set).</summary>
public static class JobIcons
{
    public const string All = MudBlazor.Icons.Material.Filled.Home;
    public const string Development = MudBlazor.Icons.Material.Filled.Psychology;
    public const string OnApproval = MudBlazor.Icons.Material.Filled.HourglassEmpty;
    public const string Approved = MudBlazor.Icons.Material.Filled.HowToReg;
    public const string Machining = MudBlazor.Icons.Material.Filled.Earbuds;
    public const string Assembly = MudBlazor.Icons.Material.Filled.Build;
    public const string ForInvoicing = MudBlazor.Icons.Material.Filled.Done;
    public const string Billed = MudBlazor.Icons.Material.Filled.DoneAll;
    public const string Delete = MudBlazor.Icons.Material.Filled.Delete;
    public const string Late = MudBlazor.Icons.Material.Filled.RunningWithErrors;
    public const string NinePlus = MudBlazor.Icons.Material.Filled.Filter9Plus;
    public const string PriorityHigh = MudBlazor.Icons.Material.Filled.MoveUp;
    public const string PriorityMedium = MudBlazor.Icons.Material.Filled.Cancel;
    public const string PriorityLow = MudBlazor.Icons.Material.Filled.MoveDown;

    /// <summary>Filter1 … Filter9.</summary>
    public static string Digit(int days) => days switch
    {
        1 => MudBlazor.Icons.Material.Filled.Filter1,
        2 => MudBlazor.Icons.Material.Filled.Filter2,
        3 => MudBlazor.Icons.Material.Filled.Filter3,
        4 => MudBlazor.Icons.Material.Filled.Filter4,
        5 => MudBlazor.Icons.Material.Filled.Filter5,
        6 => MudBlazor.Icons.Material.Filled.Filter6,
        7 => MudBlazor.Icons.Material.Filled.Filter7,
        8 => MudBlazor.Icons.Material.Filled.Filter8,
        _ => MudBlazor.Icons.Material.Filled.Filter9,
    };
}

/// <summary>The template labels of the "Imprimir etiquetas" dialog (legacy <c>_constants/printLabelspt.js</c>).</summary>
public static class PrintTemplateLabels
{
    /// <summary>The <c>producao</c> template.</summary>
    public const string Production = "Produção";

    /// <summary>The <c>maquinacao</c> template.</summary>
    public const string Machining = "Maquinação";
}
