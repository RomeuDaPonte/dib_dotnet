using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Navigation;

/// <summary>
/// The landing menu (legacy <c>menuListItem.jsx</c>): label, route and the role that shows it, in the legacy order.
/// Shown per the user's <b>current</b> roles (refreshed from <c>/api/account/me</c>, PLAN.md §4.6).
/// </summary>
public static class MenuItems
{
    /// <summary>D15: in the legacy UI only, not in <c>GET /api/account/roles</c>.</summary>
    public const string CanSeeAdjudicacoes = "canSeeAdjudicacoes";

    public static IReadOnlyList<MenuItem> All { get; } =
    [
        new("Dashboard", "landing/dashboard", RoleNames.CanSeeDashboard),
        new("Orçamentos", "landing/budgets", RoleNames.CanSeeOrcamentos),
        new("Obras", "landing/job", RoleNames.CanSeeObras),
        new("Adjudicações", "landing/jobReceivings", CanSeeAdjudicacoes),
        new("Entidades", "landing/entity", RoleNames.CanSeeEntidades),
        new("Leituras", "landing/leituras", RoleNames.CanSeeLeituras),
        new("Definições", "landing/settings", RoleNames.CanSeeDefinicoes),
    ];

    public static IEnumerable<MenuItem> For(IReadOnlyCollection<string> roles) =>
        All.Where(item => roles.Contains(item.Role, StringComparer.Ordinal));
}

public sealed record MenuItem(string Label, string Path, string Role);
