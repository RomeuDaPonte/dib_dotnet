namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>
/// User roles (legacy <c>helpers/roleName.js</c>), stored in <c>users.roles[]</c>. Imported arrays also hold
/// <c>null</c> elements (PLAN.md §4.5 item 2): role checks skip them.
/// </summary>
public static class RoleNames
{
    public const string CanSeeOrcamentos = "canSeeOrcamentos";
    public const string CanSeeObras = "canSeeObras";
    public const string CanSeeHistorico = "canSeeHistorico";
    public const string CanSeeEntidades = "canSeeEntidades";

    /// <summary>Settings: users, prices, payment methods, printers, database, logs.</summary>
    public const string CanSeeDefinicoes = "canSeeDefinicoes";
    public const string CanSeeDashboard = "canSeeDashboard";
    public const string CanSeeLeituras = "canSeeLeituras";

    /// <summary>In the legacy order (<c>getAllRoles</c>).</summary>
    public static IReadOnlyList<string> All { get; } =
        [CanSeeOrcamentos, CanSeeObras, CanSeeHistorico, CanSeeEntidades, CanSeeDefinicoes, CanSeeDashboard, CanSeeLeituras];
}
