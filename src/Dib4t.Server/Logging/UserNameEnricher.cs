using Serilog.Core;
using Serilog.Events;

namespace Dib4t.Server.Logging;

/// <summary>The signed-in user's name on every entry of a request (PLAN.md §3.3); nothing outside requests.</summary>
public sealed class UserNameEnricher(IHttpContextAccessor accessor) : ILogEventEnricher
{
    public const string PropertyName = "UserName";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (accessor.HttpContext?.User.Identity?.Name is { Length: > 0 } name)
        {
            logEvent.AddPropertyIfAbsent(new LogEventProperty(PropertyName, new ScalarValue(name)));
        }
    }
}
