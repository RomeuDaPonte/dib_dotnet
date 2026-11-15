using Dib4t.Client.Forms;
using Dib4t.Contracts.Settings;
using Dib4t.SharedKernel;

namespace Dib4t.Client.Settings;

/// <summary>
/// The prices dialog (legacy <c>settings/pricesModal.jsx</c>, yup <c>pricesSchema</c>: every value
/// <c>number().required()</c>, messages by key name). The fields are texts, sent as typed (<see cref="PricesRequest"/>).
/// </summary>
public sealed class PricesForm
{
    /// <summary>The dialog's fields, in its order: key and label.</summary>
    public static IReadOnlyList<(string Key, string Label)> Fields { get; } =
    [
        ("margin", "Margem"),
        ("automation", "Automação"),
        ("consultancy", "Consultoria"),
        ("development", "Desenvolvimento"),
        ("machining", "Maquinação"),
        ("assembly", "Montagem"),
    ];

    public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

    public static PricesForm From(PricesResponse prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var form = new PricesForm();
        form.Values["margin"] = Text(prices.Margin);
        form.Values["automation"] = Text(prices.Automation);
        form.Values["consultancy"] = Text(prices.Consultancy);
        form.Values["development"] = Text(prices.Development);
        form.Values["machining"] = Text(prices.Machining);
        form.Values["assembly"] = Text(prices.Assembly);
        return form;
    }

    /// <summary>yup: a missing value is required; a text that is not a number is yup's type error.</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, _) in Fields)
        {
            var text = Values.GetValueOrDefault(key);
            if (text is null)
            {
                errors[key] = Yup.Required(key);
            }
            else if (Yup.Number(text) is null)
            {
                errors[key] = Yup.NumberTypeError(key, text);
            }
        }

        return errors;
    }

    public PricesRequest ToRequest() => new(
        Values["automation"] ?? string.Empty,
        Values["consultancy"] ?? string.Empty,
        Values["development"] ?? string.Empty,
        Values["machining"] ?? string.Empty,
        Values["margin"] ?? string.Empty,
        Values["assembly"] ?? string.Empty);

    private static string? Text(double? value) => value is { } number ? JsNumber.ToString(number) : null;
}
