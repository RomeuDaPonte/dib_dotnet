namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>Business entity types (legacy <c>helpers/entityTypes.js</c>), stored in <c>entities.type</c>.</summary>
public static class EntityTypes
{
    public const string Client = "Cliente";
    public const string Supplier = "Fornecedor";
    public const string ClientAndSupplier = "Cliente e Fornecedor";

    /// <summary>In the legacy order (<c>getAllEntityTypes</c>, ids 1–3).</summary>
    public static IReadOnlyList<string> All { get; } = [Client, Supplier, ClientAndSupplier];
}
