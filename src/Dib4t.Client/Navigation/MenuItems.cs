using Dib4t.SharedKernel.Vocabulary;
using MudBlazor;

namespace Dib4t.Client.Navigation;

/// <summary>
/// The landing menu (legacy <c>menuListItem.jsx</c>): label, route, icon and the role that shows it, in the legacy order.
/// Shown per the user's <b>current</b> roles (refreshed from <c>/api/account/me</c>, PLAN.md §4.6).
/// </summary>
public static class MenuItems
{
    /// <summary>D15: in the legacy UI only, not in <c>GET /api/account/roles</c>.</summary>
    public const string CanSeeAdjudicacoes = "canSeeAdjudicacoes";

    public static IReadOnlyList<MenuItem> All { get; } =
    [
        new("Dashboard", "landing/dashboard", RoleNames.CanSeeDashboard, Icons.Material.Filled.ShowChart),
        new("Orçamentos", "landing/budgets", RoleNames.CanSeeOrcamentos, Icons.Material.TwoTone.Description),
        new("Obras", "landing/job", RoleNames.CanSeeObras, Icons.Material.Filled.Build),
        new("Adjudicações", "landing/jobReceivings", CanSeeAdjudicacoes, Icons.Material.Filled.History),
        new("Entidades", "landing/entity", RoleNames.CanSeeEntidades, Icons.Material.Filled.People),
        new("Leituras", "landing/leituras", RoleNames.CanSeeLeituras, Icons.Material.Filled.QrCode),
        new("Definições", "landing/settings", RoleNames.CanSeeDefinicoes, Icons.Material.Filled.Settings),
    ];

    public static IEnumerable<MenuItem> For(IReadOnlyCollection<string> roles) =>
        All.Where(item => roles.Contains(item.Role, StringComparer.Ordinal));
}

/// <summary>A menu entry; <c>Icon</c> is the legacy <c>@mui/icons-material</c> icon (same Material icon set).</summary>
public sealed record MenuItem(string Label, string Path, string Role, string Icon);
