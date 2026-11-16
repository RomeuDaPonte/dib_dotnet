using System.Globalization;
using Dib4t.Application.Common;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Logs;

/// <summary>Texts of the logs API (new-app only, PLAN.md §3.3): plain-text 400s in the legacy style.</summary>
public static class LogsMessages
{
    public const string Prefix = "Filtro inválido: ";

    public static string InvalidFrom => Prefix + "data inicial (from).";

    public static string InvalidTo => Prefix + "data final (to).";

    public static string FromAfterTo => Prefix + "a data inicial é depois da data final.";

    public static string UnknownLevel(string level) => Prefix + $"nível desconhecido \"{level}\".";

    public static string UnknownCategory(string category) => Prefix + $"categoria desconhecida \"{category}\".";

    public static string InvalidCursor => Prefix + "cursor.";

    public static string InvalidLimit => Prefix + $"limit tem de ser um número de 1 a {LogsService.MaxLimit}.";

    public static string TooLong(string name, int max) => Prefix + $"{name} tem mais de {max} caracteres.";
}

/// <summary>
/// The Registos tab's queries (PLAN.md §3.3): validates the filters, then reads the log store through
/// <see cref="ILogQueryService"/>. Without <c>from</c>/<c>to</c> the range is the last 24 h up to now.
/// </summary>
public sealed class LogsService(ILogQueryService store, TimeProvider time)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;
    public const int MaxTextLength = 500;
    public const int MaxNameLength = 200;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];

    public static TimeSpan DefaultRange { get; } = TimeSpan.FromHours(24);

    public async Task<Result<LogPage>> SearchAsync(LogSearchInput input, CancellationToken cancellationToken)
    {
        var search = Parse(input, time.GetUtcNow());
        return search.IsSuccess ? await store.SearchAsync(search.Value, cancellationToken) : search.Error;
    }

    public async Task<LogFacets> FacetsAsync(CancellationToken cancellationToken) =>
        new(LogCategories.All, LogLevels.All, await store.UsersAsync(cancellationToken));

    /// <summary>The checks in parameter order; the first failing one answers.</summary>
    public static Result<LogSearch> Parse(LogSearchInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        DateTimeOffset? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(input.From))
        {
            if (!TryParseDate(input.From, out var value))
            {
                return Error.Validation(LogsMessages.InvalidFrom);
            }

            from = value;
        }

        if (!string.IsNullOrWhiteSpace(input.To))
        {
            if (!TryParseDate(input.To, out var value))
            {
                return Error.Validation(LogsMessages.InvalidTo);
            }

            to = value;
        }

        var end = to ?? now;
        var start = from ?? end - DefaultRange;
        if (start > end)
        {
            return Error.Validation(LogsMessages.FromAfterTo);
        }

        var levels = Known(input.Levels, LogLevels.All, LogsMessages.UnknownLevel);
        if (!levels.IsSuccess)
        {
            return levels.Error;
        }

        var categories = Known(input.Categories, LogCategories.All, LogsMessages.UnknownCategory);
        if (!categories.IsSuccess)
        {
            return categories.Error;
        }

        var user = Trimmed(input.User);
        var requestId = Trimmed(input.RequestId);
        var text = Trimmed(input.Text);
        if (user?.Length > MaxNameLength)
        {
            return Error.Validation(LogsMessages.TooLong("user", MaxNameLength));
        }

        if (requestId?.Length > MaxNameLength)
        {
            return Error.Validation(LogsMessages.TooLong("requestId", MaxNameLength));
        }

        if (text?.Length > MaxTextLength)
        {
            return Error.Validation(LogsMessages.TooLong("q", MaxTextLength));
        }

        LogCursor? after = null;
        if (!string.IsNullOrWhiteSpace(input.Cursor))
        {
            if (!LogCursor.TryParse(input.Cursor.Trim(), out var cursor))
            {
                return Error.Validation(LogsMessages.InvalidCursor);
            }

            after = cursor;
        }

        var limit = DefaultLimit;
        if (!string.IsNullOrWhiteSpace(input.Limit)
            && (!int.TryParse(input.Limit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            return Error.Validation(LogsMessages.InvalidLimit);
        }

        return new LogSearch(start, end, levels.Value, categories.Value, user, requestId,
            LogFullTextQuery.Translate(text), after, limit);
    }

    /// <summary>ISO 8601 date, or date and time with or without an offset (without one: UTC).</summary>
    private static bool TryParseDate(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Comma-separated or repeated values, each one of <paramref name="known"/> (any case → its stored name).</summary>
    private static Result<IReadOnlyList<string>> Known(IReadOnlyList<string> values, IReadOnlyList<string> known, Func<string, string> unknown)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var name = known.FirstOrDefault(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));
                if (name is null)
                {
                    return Error.Validation(unknown(part));
                }

                if (!result.Contains(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }
}
