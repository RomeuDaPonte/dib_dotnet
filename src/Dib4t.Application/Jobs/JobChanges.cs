using System.Text;
using System.Text.Json;
using Dib4t.Application.Common.Validation;
using Dib4t.Domain.Budgets;
using Dib4t.Domain.Jobs;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Jobs;

/// <summary>
/// The legacy job model's methods (<c>models/job.js</c>: <c>setStatistics</c>, <c>updateStateDates</c>,
/// <c>changeState</c>, <c>setNewStatistics</c>, <c>changeDueDate</c>, and the details page's
/// <c>setStatisticsDates</c>, <c>updateRegisteredValue</c>, <c>updateMachiningVerificatons</c>,
/// <c>updateAssemblyVerifications</c> and the single-property assignment) on a job as read, with Mongoose 5.13's casts
/// of the <b>original</b> request values, checked against the legacy code (<c>job-board-golden.json</c>,
/// <c>job-details-golden.json</c>). It collects the
/// assignments in the legacy order (<see cref="Assignments"/>) and what Mongoose's <c>validate()</c> would refuse at the
/// save (<see cref="Refusal"/>: the route then answers 500 and nothing is written). A JavaScript <c>TypeError</c> of
/// the legacy code (e.g. spreading a missing <c>commentType</c>) is a <see cref="JobScriptException"/>: 500, nothing written.
/// </summary>
public sealed class JobChanges
{
    private readonly List<JobAssignment> _assignments = [];
    private readonly Dictionary<string, Stored<DateTime>> _dates = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, StoredValue>> _newDates = [];
    private readonly DateTime _now;
    private bool _datesExist;
    private bool _datesCreated;
    private Stored<double> _invoicedPercent;

    public JobChanges(Job job, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(job);
        Job = job;
        _now = now;
        _invoicedPercent = job.InvoicedPercent;
        if (job.StatisticsDates is { } dates)
        {
            _datesExist = true;
            foreach (var field in JobStatisticsDates.Fields)
            {
                _dates[field] = dates.Get(field);
            }
        }
    }

    /// <summary>The job as read.</summary>
    public Job Job { get; }

    /// <summary>The first value Mongoose's save would refuse (cast, required, enum), or <c>null</c>.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The assignments in the legacy order (a new <c>jobStatisticsDates</c> last, as one value).</summary>
    public IReadOnlyList<JobAssignment> Assignments
    {
        get
        {
            if (!_datesCreated)
            {
                return _assignments;
            }

            var fields = new List<KeyValuePair<string, StoredValue>> { new("_id", StoredValue.NewId) };
            fields.AddRange(_newDates);
            return [.. _assignments, new JobAssignment("jobStatisticsDates", StoredValue.Document(fields))];
        }
    }

    /// <summary>
    /// <c>setStatistics(req.body, req.user.name)</c>: comments and comment types, then the values of the
    /// <b>current</b> state (its end date now; the costs; the state's statistic as a new sub-document of the body with
    /// <c>validatedBy</c>; the delivery date; the order data and <c>invoicedBy</c> in "Para faturação"; the billed
    /// percentage). Other states only get the comments.
    /// </summary>
    public void SetStatistics(JsonElement body, string? userName)
    {
        var values = JsObject.Properties(body);
        EnsureDates();
        Assign("comments", CastString(Get(values, "comments")));
        Assign("commentType", Spread(Get(values, "commentType")));
        switch (Job.State)
        {
            case JobStates.Development or JobStates.OnApproval or JobStates.Approved:
                Assign("components", CastNumber(Get(values, "components")));
                Assign("automation", CastNumber(Get(values, "automation")));
                SetDate(Job.State switch
                {
                    JobStates.Development => "devEndedInDate",
                    JobStates.OnApproval => "onApprovalEndedInDate",
                    _ => "approvedEndedInDate",
                });
                SetInvoicedPercent(Get(values, "invoicedPercent"));
                Assign("devStatistic", Statistic(values, [], null));
                break;
            case JobStates.Machining:
                Assign("materials", CastNumber(Get(values, "materials")));
                SetDate("machiningEndedInDate");
                if (Job.MachiningStatistic is null)
                {
                    throw new JobScriptException("TypeError: Cannot set properties of undefined (setting 'validatedBy')");
                }

                Assign("machiningStatistic", Statistic(values, MachiningChecks, userName));
                SetInvoicedPercent(Get(values, "invoicedPercent"));
                break;
            case JobStates.Assembly:
                Assign("components", CastNumber(Get(values, "components")));
                Assign("automation", CastNumber(Get(values, "automation")));
                Assign("deliveryDate", CastDate(Get(values, "deliveryDate"), "deliveryDate"));
                SetDate("assemblyEndedInDate");
                Assign("assemblyStatistic", Statistic(values, AssemblyChecks, userName));
                SetInvoicedPercent(Get(values, "invoicedPercent"));
                break;
            case JobStates.ForInvoicing:
                Assign("isOfficialOrderIdentification", CastBoolean(Get(values, "isOfficialOrderIdentification")));
                Assign("deliveryDate", CastDate(Get(values, "deliveryDate"), "deliveryDate"));
                Assign("orderIdentification", CastString(Get(values, "orderIdentification")));
                SetDate("forInvoicingEndedInDate");
                Assign("invoicedBy", userName is null ? StoredValue.Undefined : StoredValue.Text(userName));
                SetInvoicedPercent(Get(values, "invoicedPercent"));
                break;
        }
    }

    /// <summary>
    /// <c>updateStateDates(req.body.state)</c>: moving to "Aprovado", "Maquinação", "Montagem" or "Para faturação" sets
    /// the end dates of the states before it that have none (a missing or <c>null</c> date) to now.
    /// </summary>
    public void UpdateStateDates(JsonElement? newState)
    {
        EnsureDates();
        string[] earlier = Text(newState) switch
        {
            JobStates.Approved => ["onApprovalEndedInDate"],
            JobStates.Machining => ["onApprovalEndedInDate", "approvedEndedInDate"],
            JobStates.Assembly => ["onApprovalEndedInDate", "approvedEndedInDate", "machiningEndedInDate"],
            JobStates.ForInvoicing => ["onApprovalEndedInDate", "approvedEndedInDate", "machiningEndedInDate", "assemblyEndedInDate"],
            _ => [],
        };
        foreach (var field in earlier)
        {
            if (!_dates.TryGetValue(field, out var date) || !date.HasValue)
            {
                SetDate(field);
            }
        }
    }

    /// <summary>
    /// <c>changeState(newState)</c>: to "Faturada" only when the billed percentage (after this request's values) is
    /// missing or at least 100; else <c>false</c> and nothing changes (the legacy reason is
    /// <see cref="JobMessages.InvoicedBelow100"/>).
    /// </summary>
    public bool ChangeState(JsonElement? newState)
    {
        if (Text(newState) == JobStates.Billed && !_invoicedPercent.IsMissing && (_invoicedPercent.OrDefault()) < 100)
        {
            return false;
        }

        var state = CastString(newState);
        Assign("state", state);
        switch (state)
        {
            case StoredValue.UndefinedValue or StoredValue.NullValue:
                Refuse("state: Path `state` is required.");
                break;
            case StoredValue.TextValue text when !JobStates.All.Contains(text.Value, StringComparer.Ordinal):
                Refuse($"state: `{text.Value}` is not a valid enum value for path `state`.");
                break;
        }

        return true;
    }

    /// <summary>
    /// <c>setNewStatistics(tasks)</c> of a job without statistics: zero costs, the three statistics with the budgeted
    /// hours of the budget's tasks (deviation = budgeted), priority "Média".
    /// </summary>
    public void SetNewStatistics(IReadOnlyList<BudgetTask> tasks)
    {
        foreach (var field in new[] { "components", "automation", "materials", "others" })
        {
            Assign(field, StoredValue.Number(0));
        }

        var development = JobStatistics.BudgetedHours(tasks, TaskTypes.Development);
        var machining = JobStatistics.BudgetedHours(tasks, TaskTypes.Machining);
        var assembly = JobStatistics.BudgetedHours(tasks, TaskTypes.Assembly);
        if (double.IsNaN(development + machining + assembly))
        {
            Refuse("budgetedHours: Cast to Number failed for value NaN (a task without quantity)");
        }

        Assign("devStatistic", NewStatistic(development, []));
        Assign("machiningStatistic", NewStatistic(machining, MachiningChecks));
        Assign("assemblyStatistic", NewStatistic(assembly, AssemblyChecks));
        Assign("priority", StoredValue.Text(JobPriorities.Medium));
    }

    /// <summary><c>changeDueDate(req.body.dueDate)</c>: Mongoose's date cast; missing, <c>null</c> or empty fail the required check.</summary>
    public void ChangeDueDate(JsonElement? dueDate)
    {
        var value = CastDate(dueDate, "dueDate");
        Assign("dueDate", value);
        if (value is StoredValue.UndefinedValue or StoredValue.NullValue)
        {
            Refuse("dueDate: Path `dueDate` is required.");
        }
    }

    /// <summary><c>job.priority = req.body.priority</c> (Joi checked it).</summary>
    public void SetPriority(string priority) => Assign("priority", StoredValue.Text(priority));

    /// <summary>
    /// <c>setStatisticsDates(forJobState, newDate)</c> (<c>PATCH statisticsDates</c>): the end date as Mongoose casts it
    /// (<c>""</c> and <c>null</c> are stored <c>null</c>; missing removes it; invalid is refused). <b>D17 fixed</b>: a job
    /// without <c>jobStatisticsDates</c> gets a new one with this date, as <c>setStatistics</c> does (legacy: a
    /// <c>TypeError</c>, 500).
    /// </summary>
    public void SetStatisticsDate(string field, JsonElement? newDate)
    {
        ArgumentNullException.ThrowIfNull(field);
        EnsureDates();
        SetDate(field, CastDate(newDate, "jobStatisticsDates." + field));
    }

    /// <summary>
    /// <c>updateRegisteredValue(newRegisteredValue, taskType)</c> (<c>PATCH register</c>, the cost table's "Registo"):
    /// development → <c>devStatistic.numberOfHoursUsed</c>, assembly and machining → their statistic's hours,
    /// automation/components/others → the cost, <c>custoNaoQualidade</c> → <c>costNonQuality</c>, <c>materials</c>;
    /// Mongoose's number cast; any other type (also a non-string) changes nothing. <b>D18 fixed</b>: consultancy is
    /// development (legacy: nothing). A missing statistic is the legacy <c>TypeError</c> (500).
    /// </summary>
    public void SetRegisteredValue(JsonElement? newValue, JsonElement? taskType)
    {
        switch (Text(taskType))
        {
            case TaskTypes.Development or TaskTypes.Consultancy:
                SetHours("devStatistic", Job.DevStatistic is not null, newValue);
                break;
            case TaskTypes.Assembly:
                SetHours("assemblyStatistic", Job.AssemblyStatistic is not null, newValue);
                break;
            case TaskTypes.Machining:
                SetHours("machiningStatistic", Job.MachiningStatistic is not null, newValue);
                break;
            case TaskTypes.Automation:
                Assign("automation", CastNumber(newValue));
                break;
            case TaskTypes.Components:
                Assign("components", CastNumber(newValue));
                break;
            case TaskTypes.Others:
                Assign("others", CastNumber(newValue));
                break;
            case NonQualityCostType:
                Assign("costNonQuality", CastNumber(newValue));
                break;
            case MaterialsType:
                Assign("materials", CastNumber(newValue));
                break;
        }
    }

    /// <summary>The <c>taskType</c> of <c>PATCH register</c> for the non-quality cost (legacy name).</summary>
    public const string NonQualityCostType = "custoNaoQualidade";

    /// <summary>The <c>taskType</c> of <c>PATCH register</c> for the materials cost.</summary>
    public const string MaterialsType = "materials";

    /// <summary>
    /// <c>job[propertyName] = value</c> (<c>PATCH singleProperty</c>, one of <see cref="JobValidators.SingleProperties"/>)
    /// with Mongoose's cast of the path; an empty, <c>null</c> or missing order identification fails its required check.
    /// </summary>
    public void SetSingleProperty(string propertyName, JsonElement? value)
    {
        switch (propertyName)
        {
            case "deliveryDate":
                Assign("deliveryDate", CastDate(value, "deliveryDate"));
                break;
            case "orderIdentification":
                var text = CastString(value);
                Assign("orderIdentification", text);
                if (text is StoredValue.UndefinedValue or StoredValue.NullValue or StoredValue.TextValue { Value.Length: 0 })
                {
                    Refuse("orderIdentification: Path `orderIdentification` is required.");
                }

                break;
            case "isOfficialOrderIdentification":
                Assign("isOfficialOrderIdentification", CastBoolean(value));
                break;
            case "comments":
                Assign("comments", CastString(value));
                break;
            case "invoicedPercent":
                SetInvoicedPercent(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null);
        }
    }

    /// <summary>
    /// <c>req.body.updateMachiningVerificatons == true</c> (JavaScript loose equality: <c>true</c>, <c>1</c>,
    /// <c>"1"</c>, <c>[1]</c>; not <c>"true"</c>).
    /// </summary>
    public static bool IsMachiningVerification(JsonElement? flag) => flag?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => flag.Value.GetDouble() == 1,
        JsonValueKind.String => JsNumber.FromText(flag.Value.GetString()) == 1,
        JsonValueKind.Array => JsNumber.FromText(ArrayText(flag.Value)) == 1,
        _ => false,
    };

    /// <summary>
    /// <c>this.machiningStatistic = { ...requestBody.machiningStatistic }</c> (<c>PATCH updateVerifications</c>): a
    /// sub-document of the sent values for the schema's paths (defaults for missing ones; the sent <c>_id</c> kept, else
    /// a new one), <c>validatedBy</c> = the signed-in user's name (PLAN.md §7.3: legacy stores the name the client sends).
    /// </summary>
    public void ReplaceMachiningStatistic(JsonElement? statistic, string? userName)
    {
        var values = SpreadObject(statistic);
        Assign("machiningStatistic", Statistic(values, MachiningChecks, userName, SentId(values)));
    }

    /// <summary><c>this.assemblyStatistic = { ...requestBody.assemblyStatistic }</c>, as <see cref="ReplaceMachiningStatistic"/>.</summary>
    public void ReplaceAssemblyStatistic(JsonElement? statistic, string? userName)
    {
        var values = SpreadObject(statistic);
        Assign("assemblyStatistic", Statistic(values, AssemblyChecks, userName, SentId(values)));
    }

    private void SetHours(string statistic, bool exists, JsonElement? newValue)
    {
        if (!exists)
        {
            throw new JobScriptException("TypeError: Cannot set properties of undefined (setting 'numberOfHoursUsed')");
        }

        Assign(statistic + ".numberOfHoursUsed", CastNumber(newValue));
    }

    /// <summary><c>{ ...value }</c> for the schema's paths: an object's own keys; anything else has none.</summary>
    private static IReadOnlyDictionary<string, JsonElement> SpreadObject(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } element ? JsObject.Properties(element) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>The sent <c>_id</c> cast to an ObjectId (a cast failure is refused); none → a new one.</summary>
    private StoredValue SentId(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (!values.TryGetValue("_id", out var id))
        {
            return StoredValue.NewId;
        }

        if (id.ValueKind == JsonValueKind.String && LegacyObjectId.Cast(id.GetString()!) is { } hex)
        {
            return StoredValue.ObjectId(hex);
        }

        Refuse($"_id: Cast to ObjectId failed for value {id.GetRawText()}");
        return StoredValue.NewId;
    }

    private static string ArrayText(JsonElement array) =>
        string.Join(',', array.EnumerateArray().Select(e => e.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => JsNumber.ToString(e.GetDouble()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => ArrayText(e),
            _ => "[object Object]",
        }));

    /// <summary>
    /// Legacy <c>confirmDeviation(tasks, numberOfHoursUsed, deviation)</c>: no tasks of the state's type → true; else
    /// <c>Σ quantity − numberOfHoursUsed == deviation</c> with JavaScript's addition, subtraction and loose equality
    /// on the original values (<c>"18"</c> is 18).
    /// </summary>
    public static bool ConfirmDeviation(IReadOnlyList<BudgetTask> tasks, JsonElement? numberOfHoursUsed, JsonElement? deviation)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count < 1)
        {
            return true;
        }

        var sum = 0.0;
        foreach (var task in tasks)
        {
            sum += task.Quantity ?? double.NaN;
        }

        var calculated = sum - ToNumber(numberOfHoursUsed);
        return deviation is { } value && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && calculated == ToNumber(value);
    }

    /// <summary>Legacy <c>mapStateWithTaskName</c>: the task type whose hours the current state checks ("" for the others).</summary>
    public static string TaskTypeOf(string? state) => state switch
    {
        JobStates.Development or JobStates.OnApproval or JobStates.Approved => TaskTypes.Development,
        JobStates.Machining => TaskTypes.Machining,
        JobStates.Assembly => TaskTypes.Assembly,
        _ => string.Empty,
    };

    private static readonly string[] MachiningChecks = ["verificationAndShapeControl", "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish"];

    private static readonly string[] AssemblyChecks =
    [
        "pneumaticCircuits", "electricalBoardsAndCircuits", "kinematics", "programmingFeaturesPLC", "operator_equipmentSafetyConditions",
        "visualInspection", "validTryOnFirstTest",
    ];

    /// <summary>
    /// <c>this.xStatistic = { ...newStatistics }</c>: a new sub-document of the body's values for the schema's paths
    /// (hours, then the checks; a missing one its default), a new <c>_id</c>, then <c>validatedBy</c>.
    /// </summary>
    private StoredValue Statistic(IReadOnlyDictionary<string, JsonElement> values, string[] checks, string? validatedBy, StoredValue? id = null)
    {
        var fields = new List<KeyValuePair<string, StoredValue>>();
        foreach (var hours in new[] { "numberOfHoursUsed", "budgetedHours", "deviation" })
        {
            var value = CastNumber(Get(values, hours));
            fields.Add(new(hours, value is StoredValue.UndefinedValue ? StoredValue.Number(0) : value));
        }

        foreach (var check in checks)
        {
            var value = CastString(Get(values, check));
            var fallback = check == "validTryOnFirstTest" ? QuestionResponses.DefaultFirstTestTry : QuestionResponses.NotApplicable;
            fields.Add(new(check, value is StoredValue.UndefinedValue ? StoredValue.Text(fallback) : value));
            var allowed = check == "validTryOnFirstTest" ? QuestionResponses.FirstTestTries : QuestionResponses.All;
            if (value is StoredValue.TextValue text && !allowed.Contains(text.Value, StringComparer.Ordinal))
            {
                Refuse($"{check}: `{text.Value}` is not a valid enum value");
            }
        }

        fields.Add(new("_id", id ?? StoredValue.NewId));
        if (validatedBy is not null)
        {
            fields.Add(new("validatedBy", StoredValue.Text(validatedBy)));
        }

        return StoredValue.Document(fields);
    }

    private static StoredValue NewStatistic(double budgetedHours, string[] checks)
    {
        var hours = StoredValue.Number(budgetedHours);
        var fields = new List<KeyValuePair<string, StoredValue>>
        {
            new("numberOfHoursUsed", StoredValue.Number(0)),
            new("budgetedHours", hours),
            new("deviation", hours),
        };
        fields.AddRange(checks.Select(c => new KeyValuePair<string, StoredValue>(
            c, StoredValue.Text(c == "validTryOnFirstTest" ? QuestionResponses.DefaultFirstTestTry : QuestionResponses.NotApplicable))));
        fields.Add(new("_id", StoredValue.NewId));
        return StoredValue.Document(fields);
    }

    private void SetInvoicedPercent(JsonElement? value)
    {
        var cast = CastNumber(value);
        Assign("invoicedPercent", cast);
        _invoicedPercent = cast switch
        {
            StoredValue.NumberValue number => Stored<double>.Of(number.Value),
            StoredValue.NullValue => Stored<double>.Null,
            _ => Stored<double>.Missing,
        };
    }

    /// <summary><c>if (!this.jobStatisticsDates) this.jobStatisticsDates = new JobStatisticsDates()</c>.</summary>
    private void EnsureDates()
    {
        if (!_datesExist)
        {
            _datesExist = true;
            _datesCreated = true;
        }
    }

    private void SetDate(string field) => SetDate(field, StoredValue.Date(_now));

    /// <summary>
    /// <c>this.jobStatisticsDates[field] = value</c>: a field of an existing sub-document is a dotted path; in a
    /// sub-document this request creates, the field takes its place in assignment order (<c>undefined</c> leaves it out).
    /// </summary>
    private void SetDate(string field, StoredValue value)
    {
        _dates[field] = value switch
        {
            StoredValue.DateValue date => Stored<DateTime>.Of(date.Value),
            StoredValue.NullValue => Stored<DateTime>.Null,
            _ => Stored<DateTime>.Missing,
        };
        if (_datesCreated)
        {
            var index = _newDates.FindIndex(f => f.Key == field);
            if (index >= 0)
            {
                _newDates[index] = new(field, value);
            }
            else
            {
                _newDates.Add(new(field, value));
            }
        }
        else
        {
            _assignments.Add(new JobAssignment("jobStatisticsDates." + field, value));
        }
    }

    private void Assign(string path, StoredValue value) => _assignments.Add(new JobAssignment(path, value));

    private void Refuse(string reason) => Refusal ??= reason;

    private static JsonElement? Get(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? Text(JsonElement? value) => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>Mongoose <c>castString</c> (see <see cref="MongooseCast.String"/>); a refusal for an object or array.</summary>
    private StoredValue CastString(JsonElement? value)
    {
        if (value is not { } element)
        {
            return StoredValue.Undefined;
        }

        var (text, failure) = MongooseCast.String(element);
        if (failure is not null)
        {
            Refuse(failure);
            return StoredValue.Undefined;
        }

        return text is null ? StoredValue.Null : StoredValue.Text(text.Normalize(NormalizationForm.FormC));
    }

    /// <summary>
    /// Mongoose <c>castNumber</c>: <c>null</c> and <c>""</c> are <c>null</c>; a text or boolean is <c>Number(value)</c>
    /// (NaN refused); an object or array is refused.
    /// </summary>
    private StoredValue CastNumber(JsonElement? value)
    {
        if (value is not { } element)
        {
            return StoredValue.Undefined;
        }

        double number;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return StoredValue.Null;
            case JsonValueKind.String when element.GetString()!.Length == 0:
                return StoredValue.Null;
            case JsonValueKind.String:
                number = JsNumber.FromText(element.GetString());
                break;
            case JsonValueKind.Number:
                number = element.GetDouble();
                break;
            case JsonValueKind.True:
                number = 1;
                break;
            case JsonValueKind.False:
                number = 0;
                break;
            default:
                number = double.NaN;
                break;
        }

        if (double.IsNaN(number))
        {
            Refuse($"Cast to Number failed for value {element.GetRawText()}");
            return StoredValue.Undefined;
        }

        return StoredValue.Number(number);
    }

    /// <summary>Mongoose <c>castDate</c>: <c>null</c> and <c>""</c> are <c>null</c>; else <see cref="MongooseCasts.Date"/>, refused when invalid or beyond the year 9999.</summary>
    private StoredValue CastDate(JsonElement? value, string path)
    {
        if (value is not { } element)
        {
            return StoredValue.Undefined;
        }

        if (element.ValueKind == JsonValueKind.Null || (element.ValueKind == JsonValueKind.String && element.GetString()!.Length == 0))
        {
            return StoredValue.Null;
        }

        if (MongooseCasts.Date(element) is not { } time)
        {
            Refuse($"{path}: Cast to date failed for value {element.GetRawText()}");
            return StoredValue.Undefined;
        }

        if (JsDate.ToDateTime(time) is not { } date)
        {
            Refuse($"{path}: {JsDate.ToIsoString(time)} is outside the years 1–9999");
            return StoredValue.Undefined;
        }

        return StoredValue.Date(date);
    }

    private StoredValue CastBoolean(JsonElement? value)
    {
        if (value is not { } element)
        {
            return StoredValue.Undefined;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return StoredValue.Null;
        }

        if (MongooseCasts.Boolean(element) is not { } flag)
        {
            Refuse($"isOfficialOrderIdentification: Cast to Boolean failed for value {element.GetRawText()}");
            return StoredValue.Undefined;
        }

        return StoredValue.Boolean(flag);
    }

    /// <summary>
    /// <c>[...newStatistics.commentType]</c>: an array's elements, a string's characters (code points); anything else
    /// is a <c>TypeError</c>. Each element then gets Mongoose's <c>[String]</c> cast.
    /// </summary>
    private StoredValue Spread(JsonElement? value)
    {
        switch (value?.ValueKind)
        {
            case JsonValueKind.Array:
                var texts = new List<string?>();
                foreach (var element in value.Value.EnumerateArray())
                {
                    var (text, failure) = MongooseCast.String(element);
                    if (failure is not null)
                    {
                        Refuse("commentType: " + failure);
                    }

                    texts.Add(text?.Normalize(NormalizationForm.FormC));
                }

                return StoredValue.Texts(texts);
            case JsonValueKind.String:
                var characters = new List<string?>();
                var source = value.Value.GetString()!;
                for (var i = 0; i < source.Length;)
                {
                    var length = char.IsSurrogatePair(source, i) ? 2 : 1;
                    characters.Add(source.Substring(i, length));
                    i += length;
                }

                return StoredValue.Texts(characters);
            default:
                throw new JobScriptException("TypeError: newStatistics.commentType is not iterable");
        }
    }

    /// <summary>JavaScript <c>ToNumber</c> of a JSON value (<c>undefined</c> → NaN, <c>null</c> → 0).</summary>
    private static double ToNumber(JsonElement? value) => value?.ValueKind switch
    {
        null or JsonValueKind.Undefined => double.NaN,
        JsonValueKind.Null => 0,
        JsonValueKind.Number => value.Value.GetDouble(),
        JsonValueKind.String => JsNumber.FromText(value.Value.GetString()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Array when value.Value.GetArrayLength() == 0 => 0, // [] → "" → 0
        _ => double.NaN,
    };
}

/// <summary>A JavaScript error the legacy route throws before saving (Express answers 500; nothing is written).</summary>
public sealed class JobScriptException : Exception
{
    public JobScriptException()
    {
    }

    public JobScriptException(string message)
        : base(message)
    {
    }

    public JobScriptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
