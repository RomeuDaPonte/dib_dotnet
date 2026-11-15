namespace Dib4t.Application.Settings;

/// <summary>The legacy texts of <c>routes/prices.js</c>, <c>routes/printerSettings.js</c> and the payment-method routes of <c>routes/entity.js</c>, verbatim.</summary>
public static class SettingsMessages
{
    public const string PaymentMethodTaken = "Modo de pagamento com esse nome já está registado";
    public const string PaymentMethodMissing = "Modo de pagamento não existe na base de dados";
    public const string PrinterNotFound = "Printer settings not found.";
    public const string TemplateNotFound = "Template settings not found.";
}
