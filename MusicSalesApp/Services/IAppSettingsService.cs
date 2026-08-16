#nullable enable

using MusicSalesApp.Models;

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
    /// Gets the minimum confidence, 0-1, at which lyric timings are shown to listeners. Timings
    /// below it are kept and shown to the creator but withheld from players.
    /// </summary>
    Task<double> GetLyricsConfidenceThresholdAsync();

    /// <summary>Sets the minimum confidence at which lyric timings are shown to listeners.</summary>
    /// <param name="threshold">A value between 0 and 1; anything outside is clamped.</param>
    Task SetLyricsConfidenceThresholdAsync(double threshold);

    /// <summary>Whether creators are emailed when their lyric timing finishes.</summary>
    Task<bool> GetLyricsCompletionEmailsEnabledAsync();

    /// <summary>Turn the lyric timing completion email on or off for everybody.</summary>
    Task SetLyricsCompletionEmailsEnabledAsync(bool enabled);

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
    /// Whether the creator upload page sends files straight from the browser to Azure, bypassing the
    /// web server entirely.
    /// </summary>
    /// <returns><see langword="false"/> unless explicitly enabled, including on a malformed value.</returns>
    Task<bool> IsDirectToStorageUploadEnabledAsync();

    /// <summary>Turns browser-direct uploads on or off.</summary>
    Task SetDirectToStorageUploadEnabledAsync(bool enabled);

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

    /// <summary>
    /// Gets the site maintenance window start time in UTC.
    /// </summary>
    Task<DateTime?> GetSiteMaintenanceStartUtcAsync();

    /// <summary>
    /// Sets the site maintenance window start time in UTC.
    /// </summary>
    Task SetSiteMaintenanceStartUtcAsync(DateTime startUtc);

    /// <summary>
    /// Gets the site maintenance window end time in UTC.
    /// </summary>
    Task<DateTime?> GetSiteMaintenanceEndUtcAsync();

    /// <summary>
    /// Sets the site maintenance window end time in UTC.
    /// </summary>
    Task SetSiteMaintenanceEndUtcAsync(DateTime endUtc);

    /// <summary>
    /// Checks if the site maintenance notice should be displayed to users.
    /// Returns true if the end time is in the future and not DateTime.MinValue.
    /// </summary>
    Task<bool> ShouldShowSiteMaintenanceNoticeAsync();

    /// <summary>
    /// Gets the application version number.
    /// </summary>
    /// <returns>The version string, or null if not set.</returns>
    Task<string?> GetAppVersionAsync();

    /// <summary>
    /// Sets the application version number.
    /// </summary>
    /// <param name="version">The version string.</param>
    Task SetAppVersionAsync(string version);

    /// <summary>
    /// Gets the atomic snapshot of the PayPal subscription plans offered by the web app.
    /// Returns null until an administrator explicitly configures an offer.
    /// </summary>
    Task<PayPalWebSubscriptionOffer?> GetPayPalWebSubscriptionOfferAsync();

    /// <summary>
    /// Atomically replaces the PayPal web offer and assigns its next version and update time.
    /// </summary>
    Task<PayPalWebSubscriptionOffer> SetPayPalWebSubscriptionOfferAsync(PayPalWebSubscriptionOffer offer);
}
