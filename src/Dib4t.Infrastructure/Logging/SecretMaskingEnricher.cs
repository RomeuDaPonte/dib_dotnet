using Serilog.Core;
using Serilog.Events;

namespace Dib4t.Infrastructure.Logging;

/// <summary>
/// Never log secrets (PLAN.md §3.3): every property whose name contains <c>password</c>, <c>token</c>, <c>key</c>
/// or <c>secret</c> (any case) is replaced by <c>***</c>, also inside destructured objects, dictionaries and lists.
/// Runs last in the pipeline, before any sink, so the rendered message is masked too.
/// </summary>
public sealed class SecretMaskingEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    private static readonly string[] SecretNameParts = ["password", "token", "key", "secret"];
    private static readonly ScalarValue MaskValue = new(Mask);

    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var part in SecretNameParts)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        List<LogEventProperty>? replacements = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            var masked = IsSecretName(name) ? MaskValue : MaskNested(value);
            if (!ReferenceEquals(masked, value))
            {
                (replacements ??= []).Add(new LogEventProperty(name, masked));
            }
        }

        if (replacements is not null)
        {
            foreach (var property in replacements)
            {
                logEvent.AddOrUpdateProperty(property);
            }
        }
    }

    /// <summary>The same instance when nothing inside is secret (no allocation on the common path).</summary>
    private static LogEventPropertyValue MaskNested(LogEventPropertyValue value)
    {
        switch (value)
        {
            case StructureValue structure:
                {
                    LogEventProperty[]? properties = null;
                    for (var i = 0; i < structure.Properties.Count; i++)
                    {
                        var property = structure.Properties[i];
                        var masked = IsSecretName(property.Name) ? MaskValue : MaskNested(property.Value);
                        if (!ReferenceEquals(masked, property.Value))
                        {
                            properties ??= [.. structure.Properties];
                            properties[i] = new LogEventProperty(property.Name, masked);
                        }
                    }

                    return properties is null ? value : new StructureValue(properties, structure.TypeTag);
                }

            case DictionaryValue dictionary:
                {
                    var changed = false;
                    var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                    foreach (var (key, element) in dictionary.Elements)
                    {
                        var masked = key.Value is string name && IsSecretName(name) ? MaskValue : MaskNested(element);
                        changed |= !ReferenceEquals(masked, element);
                        elements.Add(new(key, masked));
                    }

                    return changed ? new DictionaryValue(elements) : value;
                }

            case SequenceValue sequence:
                {
                    var changed = false;
                    var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);
                    foreach (var element in sequence.Elements)
                    {
                        var masked = MaskNested(element);
                        changed |= !ReferenceEquals(masked, element);
                        elements.Add(masked);
                    }

                    return changed ? new SequenceValue(elements) : value;
                }

            default:
                return value;
        }
    }
}
