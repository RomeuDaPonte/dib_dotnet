namespace Dib4t.Domain.Settings;

/// <summary>A payment condition offered for entities and budgets (legacy <c>models/paymentMethods.js</c>).</summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="Name">The text, as stored (never trimmed).</param>
/// <param name="Version">The stored <c>__v</c> (answered by the legacy API).</param>
public sealed record PaymentMethod(string Id, string? Name, int Version);
