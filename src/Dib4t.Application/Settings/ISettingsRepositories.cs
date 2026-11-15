using Dib4t.Domain.Settings;

namespace Dib4t.Application.Settings;

/// <summary>The single <c>prices</c> document (legacy <c>repositories/pricesRepository.js</c>).</summary>
public interface IPricesRepository
{
    /// <summary>Legacy <c>Prices.findOne()</c>: the first document, or <c>null</c>.</summary>
    Task<Prices?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the document in the legacy field order with <c>__v: 0</c> (startup seeding).</summary>
    Task InsertAsync(Prices prices, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>prices.set({…}); prices.save()</c> on the first document: <c>$set</c> of the values that differ
    /// (numerically: a stored Int32 10 equals 10); nothing when none does. <c>false</c> when there is no document.
    /// </summary>
    Task<bool> UpdateAsync(Prices prices, CancellationToken cancellationToken);
}

/// <summary><c>paymentmethods</c> (legacy <c>repositories/paymentMethodRepositoy.js</c> and the routes in <c>routes/entity.js</c>).</summary>
public interface IPaymentMethodRepository
{
    /// <summary>Every document in natural order.</summary>
    Task<IReadOnlyList<PaymentMethod>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Legacy <c>findOne({ name })</c>: exact match.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);

    Task<PaymentMethod?> FindByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>Inserts <c>{ _id, name, __v: 0 }</c>.</summary>
    Task<PaymentMethod> InsertAsync(string name, CancellationToken cancellationToken);

    /// <summary>Legacy <c>findByIdAndDelete</c>: the deleted document, or <c>null</c>.</summary>
    Task<PaymentMethod?> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary><c>printersettings</c> (legacy <c>repositories/printerSettingsRepository.js</c>).</summary>
public interface IPrinterSettingsRepository
{
    /// <summary>Every document in natural order.</summary>
    Task<IReadOnlyList<PrinterSettings>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Legacy <c>find({ templateName })</c>.</summary>
    Task<IReadOnlyList<PrinterSettings>> ListByTemplateAsync(string templateName, CancellationToken cancellationToken);

    /// <summary>Inserts <c>{ _id, ip, port, templateName, __v: 0 }</c>.</summary>
    Task<PrinterSettings> InsertAsync(PrinterInput printer, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>update</c>: <c>$set</c> of the values that differ (nothing when none does); the document as saved,
    /// or <c>null</c> when there is none.
    /// </summary>
    Task<PrinterSettings?> UpdateAsync(string id, PrinterInput printer, CancellationToken cancellationToken);

    /// <summary>Legacy <c>findByIdAndRemove</c>: the deleted document, or <c>null</c>.</summary>
    Task<PrinterSettings?> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>A printer from a request body that passed the legacy validation.</summary>
public sealed record PrinterInput(string Ip, double Port, string TemplateName);
