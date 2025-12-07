#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing user theme preferences.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Event raised when the theme changes.
    /// </summary>
    event Action? OnThemeChanged;

    /// <summary>
    /// Gets the current theme ("Light" or "Dark").
    /// </summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Gets whether the current theme is dark.
    /// </summary>
    bool IsDarkTheme { get; }

    /// <summary>
    /// Gets the Syncfusion CSS URL for the current theme.
    /// </summary>
    string SyncfusionCssUrl { get; }

    /// <summary>
    /// Gets the custom CSS filename for the current theme.
    /// </summary>
    string CustomCssFile { get; }

    /// <summary>
    /// Sets the theme and optionally persists it for the logged-in user.
    /// </summary>
    /// <param name="theme">The theme to set ("Light" or "Dark").</param>
    /// <param name="persist">Whether to persist the theme to the database for logged-in users.</param>
    Task SetThemeAsync(string theme, bool persist = true);

    /// <summary>
    /// Initializes the theme based on the current user's preference.
    /// </summary>
    Task InitializeThemeAsync();
}
