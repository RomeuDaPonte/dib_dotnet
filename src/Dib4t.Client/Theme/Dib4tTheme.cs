using MudBlazor;

namespace Dib4t.Client.Theme;

/// <summary>
/// The legacy palettes (PLAN.md §8.3, C5; legacy <c>landing.jsx</c> <c>createMuiTheme</c>): MUI v4 dark with primary
/// orange[500] / secondary deepOrange[900] (the default, and always on the login page), and MUI v4 light with primary
/// lightBlue[500] / secondary deepPurple[500] (theme switch on). The MUI v4 AppBar uses the primary color in both modes,
/// with MUI's contrast text (dark on orange and on light blue). Font: Roboto, bundled in <c>wwwroot/fonts</c>.
/// Sizes are in rem so the browser font size scales the shell (§8.4 rule 1).
/// Translucent colors are written as hex bytes: MUI renders 0.87 as alpha byte 0xde, 0.54 as 0x8a, 0.7 as 0xb3 and
/// 0.12 as 0x1f, while MudBlazor would truncate "rgba(…,0.87)" to 0xdd (checked against the legacy UI in ShellTests).
/// </summary>
public static class Dib4tTheme
{
    /// <summary>MUI's default font stack; "Roboto" is the bundled variable font (app.css <c>@font-face</c>).</summary>
    public static IReadOnlyList<string> FontFamily { get; } = ["Roboto", "Helvetica", "Arial", "sans-serif"];

    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#ff9800",
            PrimaryContrastText = "#000000de",
            Secondary = "#bf360c",
            Background = "#303030",
            Surface = "#424242",
            AppbarBackground = "#ff9800",
            AppbarText = "#000000de",
            DrawerBackground = "#424242",
            DrawerText = "#ffffff",
            DrawerIcon = "#ffffff",
            TextPrimary = "#ffffff",
            TextSecondary = "#ffffffb3",
            Divider = "#ffffff1f",
            Error = "#f44336",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#03a9f4",
            PrimaryContrastText = "#000000de",
            Secondary = "#673ab7",
            Background = "#fafafa",
            Surface = "#ffffff",
            AppbarBackground = "#03a9f4",
            AppbarText = "#000000de",
            DrawerBackground = "#ffffff",
            DrawerText = "#000000de",
            DrawerIcon = "#0000008a",
            TextPrimary = "#000000de",
            TextSecondary = "#0000008a",
            Divider = "#0000001f",
            Error = "#f44336",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = [.. FontFamily] },
        },
        LayoutProperties = new LayoutProperties
        {
            // Legacy drawerWidth 240 px, closed theme.spacing(9) = 72 px (sm and up; the mini drawer is md and up).
            DrawerWidthLeft = "15rem",
            DrawerMiniWidthLeft = "4.5rem",
        },
    };
}
