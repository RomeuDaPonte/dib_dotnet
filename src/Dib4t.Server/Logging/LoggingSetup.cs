using System.Diagnostics;
using System.Reflection;
using Dib4t.Infrastructure.Logging;
using Dib4t.SharedKernel;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Dib4t.Server.Logging;

/// <summary>
/// The logging pipeline (PLAN.md §3.3, C11): Serilog → console + the SQLite log store, request id and user on every
/// entry, secrets masked, one <c>Http</c> entry per API request. Levels per source come from the <c>Serilog</c>
/// configuration section (<c>MinimumLevel:Override</c>).
/// </summary>
public static partial class LoggingSetup
{
    /// <summary>Response header with the request id (also on errors), so a user's error can be found in Registos.</summary>
    public const string RequestIdHeader = "x-request-id";

    public static WebApplicationBuilder AddDib4tLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSerilog(
            (services, configuration) => configuration
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .Enrich.With(new UserNameEnricher(services.GetRequiredService<IHttpContextAccessor>()))
                .Enrich.With<SecretMaskingEnricher>()
                .WriteTo.Console(
                    restrictedToMinimumLevel: services.GetRequiredService<IOptions<LogsOptions>>().Value.ConsoleMinimumLevel,
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture)
                .WriteTo.Sink(services.GetRequiredService<LogStoreSink>()),
            preserveStaticLogger: true);
        return builder;
    }

    /// <summary>
    /// Request id returned in <see cref="RequestIdHeader"/>: the request's trace id, which the log store records as
    /// the request id of every entry of the request (or, without a trace, ASP.NET Core's <c>TraceIdentifier</c>, which
    /// its request scope logs as <c>RequestId</c>). Then one entry per request from the Serilog request logging
    /// middleware. Must run first.
    /// </summary>
    public static WebApplication UseDib4tRequestLogging(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) =>
        {
            var requestId = RequestId(context);
            // OnStarting survives the exception handler's Response.Clear(), so 5xx answers carry it too.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[RequestIdHeader] = requestId;
                return Task.CompletedTask;
            });
            return next(context);
        });

        app.UseSerilogRequestLogging(options =>
        {
            // The app's logger: the static Log.Logger stays unset (preserveStaticLogger), e.g. for parallel test hosts.
            options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RouteTemplate} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = RequestLevel;
            options.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                diagnostics.Set(LogCategories.PropertyName, LogCategories.Http);
                diagnostics.Set("RouteTemplate", context.GetEndpoint() is RouteEndpoint endpoint
                    ? "/" + endpoint.RoutePattern.RawText?.TrimStart('/')
                    : context.Request.Path.Value);
            };
        });
        return app;
    }

    /// <summary>Startup (version, environment) and shutdown entries (category <c>App</c>).</summary>
    public static void LogLifetime(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dib4t.Server.Lifetime");
        var version = typeof(LoggingSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        LogStarting(logger, version, app.Environment.EnvironmentName);
        app.Lifetime.ApplicationStopping.Register(() => LogStopping(logger));
    }

    /// <summary>
    /// Only API requests are logged at Information and up ("every API request"); static files and the host page at
    /// Verbose (off). 5xx Error, 4xx Warning; browsing the logs (<c>/api/logs</c>) at Debug so it does not flood them.
    /// </summary>
    internal static LogEventLevel RequestLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        var status = context.Response.StatusCode;
        if (exception is not null || status >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return LogEventLevel.Verbose;
        }

        if (status >= StatusCodes.Status400BadRequest)
        {
            return LogEventLevel.Warning;
        }

        return path.StartsWithSegments("/api/logs", StringComparison.OrdinalIgnoreCase)
            ? LogEventLevel.Debug
            : LogEventLevel.Information;
    }

    private static string RequestId(HttpContext context) =>
        Activity.Current is { TraceId: var traceId } && traceId != default
            ? traceId.ToHexString()
            : context.TraceIdentifier;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Dib4t {Version} starting ({Environment})")]
    private static partial void LogStarting(ILogger logger, string version, string environment);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Dib4t stopping")]
    private static partial void LogStopping(ILogger logger);
}
