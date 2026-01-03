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

    /// <summary>
    /// Gets the platform commission rate for seller sales.
    /// </summary>
    /// <returns>The commission rate as a decimal (0.15 = 15%), or the default value if not set.</returns>
    Task<decimal> GetCommissionRateAsync();

    /// <summary>
    /// Sets the platform commission rate for seller sales.
    /// </summary>
    /// <param name="rate">The commission rate as a decimal (0.15 = 15%).</param>
    Task SetCommissionRateAsync(decimal rate);

    /// <summary>
    /// Gets the stream pay rate for sellers.
    /// </summary>
    /// <returns>The stream pay rate as a decimal (0.005 = $5 per 1000 streams), or the default value if not set.</returns>
    Task<decimal> GetStreamPayRateAsync();

    /// <summary>
    /// Sets the stream pay rate for sellers.
    /// </summary>
    /// <param name="rate">The stream pay rate as a decimal (0.005 = $5 per 1000 streams).</param>
    Task SetStreamPayRateAsync(decimal rate);
}
