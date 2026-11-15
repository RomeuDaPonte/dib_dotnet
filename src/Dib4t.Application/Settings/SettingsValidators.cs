using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.Domain.Settings;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Application.Settings;

/// <summary>
/// Legacy <c>validations/pricesValidation.js</c>, <c>printerSettingsValidation.js</c> and
/// <c>paymentMethodValidation.js</c> (Joi 14.3.1), and the values the models then store (a numeric text such as
/// <c>"32.5"</c>, which the legacy UI sends, is stored as the number), checked against the legacy code
/// (<c>settings-golden.json</c>). Strings the app stores are Unicode NFC (PLAN.md §4.3 rule 2).
/// </summary>
public static partial class SettingsValidators
{
    private static readonly JoiObjectSchema PricesSchema = new(
        ("automation", JoiNumber.Create().Required()),
        ("consultancy", JoiNumber.Create().Required()),
        ("development", JoiNumber.Create().Required()),
        ("margin", JoiNumber.Create().Required().Min(1).Max(100)),
        ("machining", JoiNumber.Create().Required()),
        ("assembly", JoiNumber.Create().Required()));

    private static readonly JoiObjectSchema PrinterSchema = new(
        ("ip", JoiString.Create().Ip().Required()),
        ("port", JoiNumber.Create().Required()),
        ("templateName", JoiString.Create().Valid(PrintTemplates.All).Required()));

    // joi-objectid 3.0.1: Joi.string().regex(/^[0-9a-fA-F]{24}$/, "valid mongo id").
    private static readonly JoiObjectSchema PrinterIdSchema = new(("id", JoiString.Create().Regex(MongoId(), "valid mongo id").Required()));

    private static readonly JoiObjectSchema PaymentMethodSchema = new(("name", JoiString.Create().Required()));

    /// <summary><c>POST /api/prices</c>.</summary>
    public static Result<Prices> ValidatePrices(JsonElement body)
    {
        if (PricesSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        double Number(string key) => JoiNumber.Convert(properties[key]).Value;
        return new Prices(Number("automation"), Number("consultancy"), Number("development"), Number("machining"), Number("assembly"), Number("margin"));
    }

    /// <summary><c>POST /api/printer-settings</c> and the body of <c>PUT /:id</c>.</summary>
    public static Result<PrinterInput> ValidatePrinter(JsonElement body)
    {
        if (PrinterSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        var properties = JsObject.Properties(body);
        return new PrinterInput(
            Nfc(properties["ip"].GetString()!), JoiNumber.Convert(properties["port"]).Value, properties["templateName"].GetString()!);
    }

    /// <summary>The <c>:id</c> of <c>PUT</c>/<c>DELETE /api/printer-settings/:id</c>: the 400 text, or <c>null</c>.</summary>
    public static string? ValidatePrinterId(string id)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id }));
        return PrinterIdSchema.Validate(document.RootElement);
    }

    /// <summary><c>POST /api/entity/addPaymentMethod</c>: the name to store.</summary>
    public static Result<string> ValidatePaymentMethod(JsonElement body)
    {
        if (PaymentMethodSchema.Validate(body) is { } message)
        {
            return Error.Validation(message);
        }

        return Nfc(JsObject.Properties(body)["name"].GetString()!);
    }

    private static string Nfc(string value) => value.Normalize(NormalizationForm.FormC);

    [GeneratedRegex(@"^[0-9a-fA-F]{24}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MongoId();
}
