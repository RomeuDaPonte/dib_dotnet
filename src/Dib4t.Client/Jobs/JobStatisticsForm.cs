using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Jobs;

/// <summary>
/// The statistics dialog of a state change (legacy <c>jobStatisticsModal.jsx</c>, <c>useJobStatisticsForm.js</c>,
/// <c>useStatisticsModalHook.js</c>): the form of the job's <b>current</b> state, filled from the job, and the body
/// it sends (<c>{ ...form, state: newState }</c>, the typed values as the inputs' texts, as legacy).
/// </summary>
public sealed class JobStatisticsForm
{
    private readonly Dictionary<string, object?> _values;

    private JobStatisticsForm(string jobId, string currentState, string newState, string title, Dictionary<string, object?> values)
    {
        JobId = jobId;
        CurrentState = currentState;
        NewState = newState;
        Title = title;
        _values = values;
    }

    /// <summary>The verification questions (machining, assembly): element name and legacy label.</summary>
    public static IReadOnlyList<(string Field, string Label)> MachiningQuestions { get; } =
    [
        ("verificationAndShapeControl", "Verificação e controlo de forma"),
        ("dimensionalVerificationAndControl", "Verificação e controlo dimencional"),
        ("analysisOfTheDegreeOfFinish", "Análise ao grau de acabamento"),
    ];

    public static IReadOnlyList<(string Field, string Label)> AssemblyQuestions { get; } =
    [
        ("pneumaticCircuits", "Circuitos pneumáticos"),
        ("electricalBoardsAndCircuits", "Quadros e circuitos elétricos"),
        ("kinematics", "Cinemática"),
        ("programmingFeaturesPLC", "Funcionalidades no âmbito da programação PLC"),
        ("operator_equipmentSafetyConditions", "Condições de segurança operador/equipamento"),
        ("visualInspection", "Inspeção visual"),
    ];

    public string JobId { get; }

    public string CurrentState { get; }

    public string NewState { get; }

    /// <summary>Legacy <c>dialogTitle</c>: <c>{state} | {number} | R -{revision}</c> (the revision part only above 0).</summary>
    public string Title { get; }

    /// <summary>"Min 0 max 100" while the billed percentage of a job being billed is out of range (Concluir disabled).</summary>
    public string? InvoicedError { get; private set; }

    /// <summary>Hours used / budgeted / deviation: every state but "Para faturação".</summary>
    public bool ShowsHours => CurrentState != JobStates.ForInvoicing;

    /// <summary>Components and automation (€): development, approval, approved and assembly.</summary>
    public bool ShowsComponentsAndAutomation =>
        CurrentState is JobStates.Development or JobStates.OnApproval or JobStates.Approved or JobStates.Assembly;

    public bool ShowsMaterials => CurrentState == JobStates.Machining;

    public bool ShowsDeliveryDate => CurrentState is JobStates.Assembly or JobStates.ForInvoicing;

    public bool ShowsOrderIdentification => CurrentState == JobStates.ForInvoicing;

    /// <summary>The questions of the current state (none outside machining and assembly).</summary>
    public IReadOnlyList<(string Field, string Label)> Questions => CurrentState switch
    {
        JobStates.Machining => MachiningQuestions,
        JobStates.Assembly => AssemblyQuestions,
        _ => [],
    };

    /// <summary>The "Ensaio válido" select (assembly).</summary>
    public bool ShowsFirstTest => CurrentState == JobStates.Assembly;

    /// <summary>The states with a statistics form (the six before billing).</summary>
    public static bool HasForm(string? state) =>
        state is JobStates.Development or JobStates.OnApproval or JobStates.Approved or JobStates.Machining or JobStates.Assembly or JobStates.ForInvoicing;

    /// <summary>Legacy <c>buildFormObject(job)</c>: the current state's fields, from the job.</summary>
    public static JobStatisticsForm For(JobResponse<BudgetResponse> job, string newState)
    {
        ArgumentNullException.ThrowIfNull(job);
        var revision = job.Budget?.Revision > 0 ? "| R -" + JsNumber.ToString(job.Budget.Revision) : string.Empty;
        var title = $"{job.State} | {job.Budget?.BudgetNumber} {revision}";
        return new JobStatisticsForm(job.Id, job.State, newState, title, Values(job));
    }

    /// <summary>The value of a field as its input shows it.</summary>
    public string Text(string field) => _values.TryGetValue(field, out var value) ? Display(value) : string.Empty;

    public bool IsOfficial => _values.TryGetValue("isOfficialOrderIdentification", out var value) && value is true;

    public IReadOnlyList<string> CommentTypes => _values.TryGetValue("commentType", out var value) && value is List<string> list ? list : [];

    /// <summary>
    /// Legacy <c>handleChange</c>: a negative value is ignored; the hours used recompute the deviation (budgeted −
    /// used); the billed percentage of a job being billed must be 0–100 (legacy checks every field of that dialog
    /// against 0–100, so an order reference such as "2024" disabled Concluir: only the percentage now).
    /// </summary>
    public void Set(string field, string? text)
    {
        text ??= string.Empty;
        if (JsNumber.FromText(text) < 0 && text.Trim().Length > 0)
        {
            return;
        }

        if (CurrentState == JobStates.ForInvoicing && field == "invoicedPercent")
        {
            var number = JsNumber.FromText(text);
            InvoicedError = number is > 100 or < 0 ? "Min 0 max 100" : null;
        }

        _values[field] = text;
        if (field == "numberOfHoursUsed")
        {
            var deviation = ToNumber(_values.GetValueOrDefault("budgetedHours")) - JsNumber.FromText(text);
            _values["deviation"] = double.IsNaN(deviation) ? null : deviation; // JSON.stringify(NaN) is null
        }
    }

    /// <summary>A question's answer ("Nok", "Ok", or "n/a" when both boxes are cleared) or the first-test count.</summary>
    public void SetAnswer(string field, string answer) => _values[field] = answer;

    public void SetOfficial(bool official) => _values["isOfficialOrderIdentification"] = official;

    /// <summary>Legacy <c>handleCommentTypeChange</c>: checked adds the type at the end, cleared removes it.</summary>
    public void SetCommentType(string label, bool isChecked)
    {
        var types = CommentTypes.Where(t => t != label).ToList();
        if (isChecked)
        {
            types = [.. CommentTypes, label];
        }

        _values["commentType"] = types;
    }

    /// <summary>What "Concluir" sends: the form with <c>state</c> = the chosen state.</summary>
    public Dictionary<string, object?> ToBody() => new(_values) { ["state"] = NewState };

    private static Dictionary<string, object?> Values(JobResponse<BudgetResponse> job)
    {
        var comments = job.Comments.OrDefault() ?? string.Empty;
        var commentType = job.CommentType.OfType<string>().ToList();
        var invoiced = job.InvoicedPercent.OrDefault() is > 0 and var value ? value : 0;
        switch (job.State)
        {
            case JobStates.Machining:
                var m = job.MachiningStatistic;
                return new()
                {
                    ["numberOfHoursUsed"] = m?.NumberOfHoursUsed ?? 0,
                    ["materials"] = job.Materials,
                    ["budgetedHours"] = m?.BudgetedHours ?? 0,
                    ["deviation"] = m?.Deviation ?? 0,
                    ["invoicedPercent"] = invoiced,
                    ["verificationAndShapeControl"] = m?.VerificationAndShapeControl ?? QuestionResponses.NotApplicable,
                    ["dimensionalVerificationAndControl"] = m?.DimensionalVerificationAndControl ?? QuestionResponses.NotApplicable,
                    ["analysisOfTheDegreeOfFinish"] = m?.AnalysisOfTheDegreeOfFinish ?? QuestionResponses.NotApplicable,
                    ["comments"] = comments,
                    ["commentType"] = commentType,
                };
            case JobStates.Assembly:
                var a = job.AssemblyStatistic;
                return new()
                {
                    ["numberOfHoursUsed"] = a?.NumberOfHoursUsed ?? 0,
                    ["components"] = job.Components,
                    ["automation"] = job.Automation,
                    ["budgetedHours"] = a?.BudgetedHours ?? 0,
                    ["deviation"] = a?.Deviation ?? 0,
                    ["deliveryDate"] = JobBoard.DateInput(job.DeliveryDate.HasValue ? job.DeliveryDate.Value : null),
                    ["invoicedPercent"] = invoiced,
                    ["pneumaticCircuits"] = a?.PneumaticCircuits ?? QuestionResponses.NotApplicable,
                    ["electricalBoardsAndCircuits"] = a?.ElectricalBoardsAndCircuits ?? QuestionResponses.NotApplicable,
                    ["kinematics"] = a?.Kinematics ?? QuestionResponses.NotApplicable,
                    ["programmingFeaturesPLC"] = a?.ProgrammingFeaturesPlc ?? QuestionResponses.NotApplicable,
                    ["operator_equipmentSafetyConditions"] = a?.OperatorEquipmentSafetyConditions ?? QuestionResponses.NotApplicable,
                    ["visualInspection"] = a?.VisualInspection ?? QuestionResponses.NotApplicable,
                    ["validTryOnFirstTest"] = a?.ValidTryOnFirstTest ?? QuestionResponses.DefaultFirstTestTry,
                    ["comments"] = comments,
                    ["commentType"] = commentType,
                };
            case JobStates.ForInvoicing:
                return new()
                {
                    ["orderIdentification"] = job.OrderIdentification,
                    ["isOfficialOrderIdentification"] = job.IsOfficialOrderIdentification,
                    ["deliveryDate"] = JobBoard.DateInput(job.DeliveryDate.HasValue ? job.DeliveryDate.Value : null),
                    ["invoicedPercent"] = job.InvoicedPercent.OrDefault() is > 0 and var billed ? billed : 100,
                    ["comments"] = comments,
                    ["commentType"] = commentType,
                };
            default:
                var dev = job.DevStatistic;
                return new()
                {
                    ["numberOfHoursUsed"] = dev?.NumberOfHoursUsed ?? 0,
                    ["components"] = job.Components,
                    ["automation"] = job.Automation,
                    ["invoicedPercent"] = invoiced,
                    ["budgetedHours"] = dev?.BudgetedHours ?? 0,
                    ["deviation"] = dev?.Deviation ?? 0,
                    ["comments"] = comments,
                    ["commentType"] = commentType,
                };
        }
    }

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        double number => JsNumber.ToString(number),
        string text => text,
        _ => value.ToString() ?? string.Empty,
    };

    private static double ToNumber(object? value) => value switch
    {
        double number => number,
        string text => JsNumber.FromText(text),
        _ => 0,
    };
}

/// <summary>
/// The comments dialog (legacy <c>jobCommentsModal.jsx</c>): the comment types and text of the job, saved through the
/// statistics route with the job's own state and values (<c>buildPostObject</c>).
/// </summary>
/// <remarks>
/// <b>Fixed legacy UI defect</b>: legacy leaves the billed percentage out of the body, so the save is refused on a job in
/// machining or assembly ("invoicedPercent" is required) and removes the percentage of a job in development or
/// approval; and it sends 100 for a job being billed at 0 %. The job's own percentage is sent instead.
/// </remarks>
public sealed class JobCommentsForm
{
    private readonly JobResponse<BudgetResponse> _job;

    public JobCommentsForm(JobResponse<BudgetResponse> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _job = job;
        Comments = job.Comments.OrDefault() ?? string.Empty;
        CommentTypes = [.. job.CommentType.OfType<string>()];
    }

    public string JobId => _job.Id;

    public string Comments { get; set; }

    public List<string> CommentTypes { get; private set; }

    public void SetCommentType(string label, bool isChecked) =>
        CommentTypes = isChecked ? [.. CommentTypes, label] : [.. CommentTypes.Where(t => t != label)];

    /// <summary>Legacy <c>buildPostObject</c> (with the billed percentage, see the remarks).</summary>
    public Dictionary<string, object?> ToBody()
    {
        var j = _job;
        var body = new Dictionary<string, object?>
        {
            ["state"] = j.State,
            ["numberOfHoursUsed"] = j.DevStatistic?.NumberOfHoursUsed ?? 0,
            ["components"] = j.Components,
            ["automation"] = j.Automation,
            ["budgetedHours"] = j.DevStatistic?.BudgetedHours ?? 0,
            ["deviation"] = j.DevStatistic?.Deviation ?? 0,
            ["comments"] = Comments,
            ["commentType"] = CommentTypes,
            ["invoicedPercent"] = j.InvoicedPercent.HasValue ? j.InvoicedPercent.Value : 0,
        };
        var delivery = JobBoard.DateInput(j.DeliveryDate.HasValue ? j.DeliveryDate.Value : null);
        switch (j.State)
        {
            case JobStates.Machining:
                var m = j.MachiningStatistic;
                body["numberOfHoursUsed"] = m?.NumberOfHoursUsed ?? 0;
                body["materials"] = j.Materials;
                body["budgetedHours"] = m?.BudgetedHours ?? 0;
                body["deviation"] = m?.Deviation ?? 0;
                body["verificationAndShapeControl"] = m?.VerificationAndShapeControl ?? QuestionResponses.NotApplicable;
                body["dimensionalVerificationAndControl"] = m?.DimensionalVerificationAndControl ?? QuestionResponses.NotApplicable;
                body["analysisOfTheDegreeOfFinish"] = m?.AnalysisOfTheDegreeOfFinish ?? QuestionResponses.NotApplicable;
                body.Remove("components");
                body.Remove("automation");
                break;
            case JobStates.Assembly:
                var a = j.AssemblyStatistic;
                body["numberOfHoursUsed"] = a?.NumberOfHoursUsed ?? 0;
                body["budgetedHours"] = a?.BudgetedHours ?? 0;
                body["deviation"] = a?.Deviation ?? 0;
                body["deliveryDate"] = delivery;
                body["pneumaticCircuits"] = a?.PneumaticCircuits ?? QuestionResponses.NotApplicable;
                body["electricalBoardsAndCircuits"] = a?.ElectricalBoardsAndCircuits ?? QuestionResponses.NotApplicable;
                body["kinematics"] = a?.Kinematics ?? QuestionResponses.NotApplicable;
                body["programmingFeaturesPLC"] = a?.ProgrammingFeaturesPlc ?? QuestionResponses.NotApplicable;
                body["operator_equipmentSafetyConditions"] = a?.OperatorEquipmentSafetyConditions ?? QuestionResponses.NotApplicable;
                body["visualInspection"] = a?.VisualInspection ?? QuestionResponses.NotApplicable;
                body["validTryOnFirstTest"] = a?.ValidTryOnFirstTest ?? QuestionResponses.DefaultFirstTestTry;
                break;
            case JobStates.ForInvoicing:
                body["orderIdentification"] = j.OrderIdentification;
                body["isOfficialOrderIdentification"] = j.IsOfficialOrderIdentification;
                body["deliveryDate"] = delivery;
                foreach (var key in new[] { "numberOfHoursUsed", "components", "automation", "budgetedHours", "deviation" })
                {
                    body.Remove(key);
                }

                break;
        }

        return body;
    }
}
