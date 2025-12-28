#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing application settings stored in the database.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Gets a setting value by key.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <returns>The setting value, or null if not found.</returns>
    Task<string?> GetSettingAsync(string key);

    /// <summary>
    /// Sets a setting value by key. Creates if not exists, updates if exists.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The setting value.</param>
    /// <param name="description">Optional description of the setting.</param>
    Task SetSettingAsync(string key, string value, string? description = null);

    /// <summary>
    /// Gets the subscription price setting.
    /// </summary>
    /// <returns>The subscription price as a decimal, or the default value if not set.</returns>
    Task<decimal> GetSubscriptionPriceAsync();

    /// <summary>
    /// Sets the subscription price setting.
    /// </summary>
    /// <param name="price">The subscription price.</param>
    Task SetSubscriptionPriceAsync(decimal price);
}
