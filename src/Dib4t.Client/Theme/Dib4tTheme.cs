using MudBlazor;

namespace Dib4t.Client.Theme;

/// <summary>
/// The legacy dark palette (PLAN.md §8.3, C5): MUI v4 dark with primary orange[500] and secondary deepOrange[900].
/// The login page is always dark. The light palette and the theme switch come with the shell.
/// </summary>
public static class Dib4tTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#ff9800",
            PrimaryContrastText = "rgba(0,0,0,0.87)",
            Secondary = "#bf360c",
            Background = "#303030",
            Surface = "#424242",
            AppbarBackground = "#ff9800",
            AppbarText = "rgba(0,0,0,0.87)",
            DrawerBackground = "#424242",
            TextPrimary = "#ffffff",
            TextSecondary = "rgba(255,255,255,0.7)",
            Divider = "rgba(255,255,255,0.12)",
            Error = "#f44336",
        },
    };
}
