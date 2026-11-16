using System.Globalization;
using System.Text;
using System.Text.Json;
using Dib4t.Client.Api;
using Dib4t.Contracts.Logs;
using Dib4t.SharedKernel;

namespace Dib4t.Client.Settings;

/// <summary>Time range presets of the Registos tab (PLAN.md §3.3); every one but <see cref="Custom"/> ends now.</summary>
public enum LogRange
{
    LastHour,
    Today,
    Last24Hours,
    Last7Days,
    Last30Days,
    Custom,
}

/// <summary>
/// State of the Registos tab (PLAN.md §3.3): the filters, the loaded pages (100 entries each, "Carregar mais" with the
/// server's keyset cursor), expanded rows and the optional auto-refresh (every 10 s, only for ranges that end now; it
/// reloads the first page). Times are shown in <see cref="Zone"/> (the browser's time zone).
/// </summary>
public sealed class LogsView(ILogsApi api, TimeProvider time) : IDisposable
{
    public const int PageSize = 100;
    public const string TimeFormat = "dd/MM/yyyy HH:mm:ss.fff";

    /// <summary>The <c>datetime-local</c> input format of the custom range.</summary>
    public const string InputFormat = "yyyy-MM-ddTHH:mm";

    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<(LogRange Value, string Text)> Ranges { get; } =
    [
        (LogRange.LastHour, "Última hora"),
        (LogRange.Today, "Hoje"),
        (LogRange.Last24Hours, "Últimas 24 horas"),
        (LogRange.Last7Days, "Últimos 7 dias"),
        (LogRange.Last30Days, "Últimos 30 dias"),
        (LogRange.Custom, "Personalizado"),
    ];

    /// <summary>The levels offered as filters (Verbose is never stored by the app's configuration).</summary>
    public static IReadOnlyList<string> Levels { get; } = [LogLevels.Fatal, LogLevels.Error, LogLevels.Warning, LogLevels.Information, LogLevels.Debug];

    private ITimer? _timer;

    /// <summary>Raised by the auto-refresh timer; the component runs <see cref="RefreshAsync"/> on its renderer.</summary>
    public event Func<Task>? RefreshDue;

    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public LogRange Range { get; set; } = LogRange.Last24Hours;

    /// <summary>Custom range start/end in <see cref="InputFormat"/>, local time.</summary>
    public string? CustomFrom { get; set; }

    public string? CustomTo { get; set; }

    public HashSet<string> SelectedLevels { get; } = new(StringComparer.Ordinal);

    public HashSet<string> SelectedCategories { get; } = new(StringComparer.Ordinal);

    public string? User { get; set; }

    public string? RequestId { get; set; }

    public string? Text { get; set; }

    public bool AutoRefresh => _timer is not null;

    public bool CanAutoRefresh => Range != LogRange.Custom;

    public IReadOnlyList<string> Categories { get; private set; } = LogCategories.All;

    public IReadOnlyList<string> Users { get; private set; } = [];

    public List<LogEntryResponse> Items { get; } = [];

    public string? NextCursor { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>The API's 4xx text (e.g. "Filtro inválido: …"), shown above the results.</summary>
    public string? Error { get; private set; }

    public HashSet<long> Expanded { get; } = [];

    public static string LevelLabel(string level) => level switch
    {
        LogLevels.Fatal => "Fatal",
        LogLevels.Error => "Erro",
        LogLevels.Warning => "Aviso",
        LogLevels.Information => "Informação",
        LogLevels.Debug => "Debug",
        _ => level,
    };

    public async Task InitializeAsync()
    {
        var facets = await api.FacetsAsync();
        if (facets.IsSuccess)
        {
            Categories = facets.Value!.Categories;
            Users = facets.Value.Users;
        }

        await SearchAsync();
    }

    /// <summary>The first page for the current filters.</summary>
    public Task SearchAsync() => LoadAsync(null);

    public Task LoadMoreAsync() => NextCursor is null ? Task.CompletedTask : LoadAsync(NextCursor);

    /// <summary>Every entry of one request (its HTTP line, Mongo commands, errors…): the other filters are cleared.</summary>
    public Task ShowRequestAsync(string requestId)
    {
        RequestId = requestId;
        SelectedLevels.Clear();
        SelectedCategories.Clear();
        User = null;
        Text = null;
        return SearchAsync();
    }

    /// <summary>The auto-refresh run: the first page again, keeping expanded rows; skipped while a load runs.</summary>
    public Task RefreshAsync() => Loading || !CanAutoRefresh ? Task.CompletedTask : SearchAsync();

    public void SetAutoRefresh(bool on)
    {
        _timer?.Dispose();
        _timer = on && CanAutoRefresh
            ? time.CreateTimer(_ => RefreshDue?.Invoke(), null, RefreshInterval, RefreshInterval)
            : null;
    }

    public void SetRange(LogRange range)
    {
        Range = range;
        if (range == LogRange.Custom)
        {
            var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime;
            CustomFrom ??= now.AddHours(-24).ToString(InputFormat, CultureInfo.InvariantCulture);
            CustomTo ??= now.ToString(InputFormat, CultureInfo.InvariantCulture);
            SetAutoRefresh(false);
        }
    }

    public static void Toggle(HashSet<string> set, string value, bool on)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (on)
        {
            set.Add(value);
        }
        else
        {
            set.Remove(value);
        }
    }

    public void ToggleExpanded(long id)
    {
        if (!Expanded.Remove(id))
        {
            Expanded.Add(id);
        }
    }

    public string FormatTime(DateTimeOffset timestamp) =>
        TimeZoneInfo.ConvertTime(timestamp, Zone).ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>The properties JSON, indented for reading (as stored when it is not valid JSON).</summary>
    public static string FormatProperties(string properties)
    {
        try
        {
            using var document = JsonDocument.Parse(properties);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return properties;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The <c>GET /api/logs</c> query string for the current filters (without <c>?</c>).</summary>
    public string BuildQuery(string? cursor)
    {
        var query = new StringBuilder();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                query.Append(query.Length == 0 ? string.Empty : "&").Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
            }
        }

        var (from, to) = TimeRange();
        Add("from", from?.ToString("O", CultureInfo.InvariantCulture));
        Add("to", to?.ToString("O", CultureInfo.InvariantCulture));
        Add("levels", string.Join(',', Levels.Where(SelectedLevels.Contains)));
        Add("categories", string.Join(',', LogCategories.All.Where(SelectedCategories.Contains)));
        Add("user", User);
        Add("requestId", RequestId);
        Add("q", Text);
        Add("cursor", cursor);
        Add("limit", PageSize.ToString(CultureInfo.InvariantCulture));
        return query.ToString();
    }

    /// <summary>UTC start and end; an end of <see langword="null"/> means now (the server's clock).</summary>
    public (DateTimeOffset? From, DateTimeOffset? To) TimeRange()
    {
        var now = time.GetUtcNow();
        return Range switch
        {
            LogRange.LastHour => (now.AddHours(-1), null),
            LogRange.Today => (LocalMidnight(now), null),
            LogRange.Last7Days => (now.AddDays(-7), null),
            LogRange.Last30Days => (now.AddDays(-30), null),
            LogRange.Custom => (ParseLocal(CustomFrom), ParseLocal(CustomTo)),
            _ => (now.AddHours(-24), null),
        };
    }

    public void Dispose() => _timer?.Dispose();

    private async Task LoadAsync(string? cursor)
    {
        Loading = true;
        try
        {
            var result = await api.SearchAsync(BuildQuery(cursor));
            if (!result.IsSuccess)
            {
                Error = result.IsUnexpected ? null : result.ErrorText;
                return;
            }

            Error = null;
            if (cursor is null)
            {
                Items.Clear();
            }

            Items.AddRange(result.Value!.Items);
            NextCursor = result.Value.NextCursor;
        }
        finally
        {
            Loading = false;
        }
    }

    private DateTimeOffset LocalMidnight(DateTimeOffset now)
    {
        var midnight = TimeZoneInfo.ConvertTime(now, Zone).Date;
        return ToUtc(midnight);
    }

    private DateTimeOffset? ParseLocal(string? text) =>
        DateTime.TryParseExact(text, InputFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? ToUtc(local) : null;

    private DateTimeOffset ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
