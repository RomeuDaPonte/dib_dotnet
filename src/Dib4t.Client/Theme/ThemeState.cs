namespace Dib4t.Client.Theme;

/// <summary>
/// Dark or light, as the app bar's theme switch sets it (legacy <c>landing.jsx</c> <c>darkState</c>, PLAN.md §8.3).
/// Like legacy, it is not stored: the landing shell starts dark every time it opens (after a login or a reload), and
/// the login page is always dark. <c>MainLayout</c>'s theme provider follows <see cref="Changed"/>.
/// </summary>
public sealed class ThemeState
{
    /// <summary>Raised when <see cref="IsLight"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The switch is on: legacy light palette. Off (default): dark.</summary>
    public bool IsLight { get; private set; }

    public bool IsDarkMode => !IsLight;

    public void SetLight(bool light)
    {
        if (IsLight == light)
        {
            return;
        }

        IsLight = light;
        Changed?.Invoke();
    }
}
