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
    /// Gets the stream pay rate for creators.
    /// </summary>
    /// <returns>The stream pay rate as a decimal (0.005 = $5 per 1000 streams), or the default value if not set.</returns>
    Task<decimal> GetStreamPayRateAsync();

    /// <summary>
    /// Sets the stream pay rate for creators.
    /// </summary>
    /// <param name="rate">The stream pay rate as a decimal (0.005 = $5 per 1000 streams).</param>
    Task SetStreamPayRateAsync(decimal rate);

    /// <summary>
    /// Gets the number of continuous seconds of playback that qualifies as a stream.
    /// </summary>
    /// <returns>The stream qualifying seconds, or the default value (30) if not set.</returns>
    Task<int> GetStreamQualifyingSecondsAsync();

    /// <summary>
    /// Sets the number of continuous seconds of playback that qualifies as a stream.
    /// </summary>
    /// <param name="seconds">The number of seconds.</param>
    Task SetStreamQualifyingSecondsAsync(int seconds);

    /// <summary>
    /// Gets whether the Tax Bandits maintenance window display is enabled.
    /// </summary>
    Task<bool> GetTaxBanditsMaintenanceEnabledAsync();

    /// <summary>
    /// Sets whether the Tax Bandits maintenance window display is enabled.
    /// </summary>
    Task SetTaxBanditsMaintenanceEnabledAsync(bool enabled);

    /// <summary>
    /// Gets the Tax Bandits maintenance window start time in UTC.
    /// </summary>
    Task<DateTime?> GetTaxBanditsMaintenanceStartUtcAsync();

    /// <summary>
    /// Sets the Tax Bandits maintenance window start time in UTC.
    /// </summary>
    Task SetTaxBanditsMaintenanceStartUtcAsync(DateTime startUtc);

    /// <summary>
    /// Gets the Tax Bandits maintenance window end time in UTC.
    /// </summary>
    Task<DateTime?> GetTaxBanditsMaintenanceEndUtcAsync();

    /// <summary>
    /// Sets the Tax Bandits maintenance window end time in UTC.
    /// </summary>
    Task SetTaxBanditsMaintenanceEndUtcAsync(DateTime endUtc);

    /// <summary>
    /// Gets the maximum audio upload file size in MB.
    /// </summary>
    /// <returns>The max upload size in MB, or the default value (100) if not set.</returns>
    Task<int> GetMaxAudioUploadSizeMBAsync();

    /// <summary>
    /// Sets the maximum audio upload file size in MB.
    /// </summary>
    /// <param name="sizeMB">The max upload size in MB.</param>
    Task SetMaxAudioUploadSizeMBAsync(int sizeMB);

    /// <summary>
    /// Gets the maximum image upload file size in MB.
    /// </summary>
    /// <returns>The max upload size in MB, or the default value (20) if not set.</returns>
    Task<int> GetMaxImageUploadSizeMBAsync();

    /// <summary>
    /// Sets the maximum image upload file size in MB.
    /// </summary>
    /// <param name="sizeMB">The max upload size in MB.</param>
    Task SetMaxImageUploadSizeMBAsync(int sizeMB);

    /// <summary>
    /// Checks if Tax Bandits is currently in a maintenance window.
    /// Returns true if maintenance is enabled and the current UTC time falls within the start/end range.
    /// </summary>
    Task<bool> IsTaxBanditsMaintenanceActiveAsync();

    /// <summary>
    /// Checks if the Tax Bandits maintenance window should be displayed to users.
    /// Returns true if maintenance is enabled and the end time is in the future.
    /// </summary>
    Task<bool> ShouldShowTaxBanditsMaintenanceWarningAsync();
}
