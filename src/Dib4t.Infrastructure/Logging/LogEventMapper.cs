using System.Globalization;
using Dib4t.SharedKernel;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// Serilog event → store row, on the background writer (not on the request path). The category comes from an
/// explicit <c>Category</c> property or from the source context (<see cref="LogCategoryMap"/>). The request id is the
/// event's trace id (the W3C trace of the HTTP request, also returned in <c>x-request-id</c>, and flowing into its
/// Mongo commands); without a trace, the <c>RequestId</c> property (ASP.NET Core's request scope).
/// </summary>
internal static class LogEventMapper
{
    public const string SourceContextProperty = "SourceContext";
    public const string RequestIdProperty = "RequestId";
    public const string UserNameProperty = "UserName";

    private static readonly JsonValueFormatter JsonFormatter = new(typeTagName: null);

    public static LogEntry ToEntry(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var properties = logEvent.Properties;
        var sourceContext = StringProperty(properties, SourceContextProperty);

        return new LogEntry(
            logEvent.Timestamp.ToUnixTimeMilliseconds(),
            logEvent.Level.ToString(),
            StringProperty(properties, LogCategories.PropertyName) ?? LogCategoryMap.Resolve(sourceContext),
            sourceContext,
            RenderMessage(logEvent),
            logEvent.Exception?.ToString(),
            PropertiesJson(properties),
            logEvent.TraceId?.ToHexString() ?? StringProperty(properties, RequestIdProperty),
            StringProperty(properties, UserNameProperty));
    }

    /// <summary>The entry written when the queue had to drop events (PLAN.md §3.3).</summary>
    public static LogEntry Dropped(long count, DateTimeOffset now) => new(
        now.ToUnixTimeMilliseconds(),
        nameof(LogEventLevel.Warning),
        LogCategories.App,
        typeof(LogStoreSink).FullName,
        string.Create(CultureInfo.InvariantCulture, $"{count} log entries dropped"),
        null,
        string.Create(CultureInfo.InvariantCulture, $$"""{"Dropped":{{count}}}"""),
        null,
        null);

    /// <summary>
    /// The message as people read it in Registos: like Serilog's rendering, but string values without quotes
    /// (<c>HTTP POST /api/… responded 200</c>, not <c>HTTP "POST" "/api/…"</c>). Other values render as Serilog does.
    /// </summary>
    internal static string RenderMessage(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property
                && property.Alignment is null
                && logEvent.Properties.TryGetValue(property.PropertyName, out var value)
                && value is ScalarValue { Value: string text })
            {
                writer.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, writer, CultureInfo.InvariantCulture);
            }
        }

        return writer.ToString();
    }

    private static string? StringProperty(IReadOnlyDictionary<string, LogEventPropertyValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text } ? text : null;

    private static bool IsColumn(string name) =>
        name is SourceContextProperty or LogCategories.PropertyName or RequestIdProperty or UserNameProperty;

    private static string? PropertiesJson(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
    {
        if (properties.Keys.All(IsColumn))
        {
            return null;
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.Write('{');
        var first = true;
        foreach (var (name, value) in properties)
        {
            if (IsColumn(name))
            {
                continue;
            }

            if (!first)
            {
                writer.Write(',');
            }

            first = false;
            JsonValueFormatter.WriteQuotedJsonString(name, writer);
            writer.Write(':');
            JsonFormatter.Format(value, writer);
        }

        writer.Write('}');
        return writer.ToString();
    }
}

/// <summary>
/// Source context (logger name) → category, by namespace prefix, in one place (PLAN.md §3.3). Anything else is
/// <see cref="LogCategories.App"/>. Feature slices add their prefixes here (or log an explicit <c>Category</c>).
/// </summary>
internal static class LogCategoryMap
{
    private static readonly (string Prefix, string Category)[] Prefixes =
    [
        ("Serilog.AspNetCore.RequestLoggingMiddleware", LogCategories.Http),
        ("Dib4t.Infrastructure.Mongo", LogCategories.Database),
        ("Dib4t.Infrastructure.Backup", LogCategories.Database),
        ("Dib4t.Application.Database", LogCategories.Database),
        ("Dib4t.Application.Account", LogCategories.Auth),
        ("Dib4t.Server.Auth", LogCategories.Auth),
        ("Microsoft.AspNetCore.Authentication", LogCategories.Auth),
        ("Microsoft.AspNetCore.Authorization", LogCategories.Auth),
    ];

    public static string Resolve(string? sourceContext)
    {
        if (sourceContext is null)
        {
            return LogCategories.App;
        }

        foreach (var (prefix, category) in Prefixes)
        {
            if (sourceContext.StartsWith(prefix, StringComparison.Ordinal)
                && (sourceContext.Length == prefix.Length || sourceContext[prefix.Length] == '.'))
            {
                return category;
            }
        }

        return LogCategories.App;
    }
}
