using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing application settings stored in the database.
/// </summary>
public class AppSettingsService : IAppSettingsService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<AppSettingsService> _logger;

    /// <summary>
    /// The key used for storing the subscription price setting.
    /// </summary>
    public const string SubscriptionPriceKey = "SubscriptionPrice";

    /// <summary>
    /// Default subscription price if not set in the database.
    /// </summary>
    public const decimal DefaultSubscriptionPrice = 3.99m;

    /// <summary>
    /// The key used for storing the stream pay rate setting.
    /// </summary>
    public const string StreamPayRateKey = "StreamPayRate";

    /// <summary>
    /// Default stream pay rate if not set in the database ($5 per 1000 streams = $0.005 per stream).
    /// </summary>
    public const decimal DefaultStreamPayRate = 0.005m;

    /// <summary>
    /// The key used for storing the stream qualifying seconds setting.
    /// </summary>
    public const string StreamQualifyingSecondsKey = "StreamQualifyingSeconds";

    /// <summary>
    /// Default number of continuous seconds of playback that qualifies as a stream.
    /// </summary>
    public const int DefaultStreamQualifyingSeconds = 30;

    /// <summary>
    /// The key used for storing whether the Tax Bandits maintenance window is enabled.
    /// </summary>
    public const string TaxBanditsMaintenanceEnabledKey = "TaxBanditsMaintenanceEnabled";

    /// <summary>
    /// The key used for storing the Tax Bandits maintenance window start time (UTC).
    /// </summary>
    public const string TaxBanditsMaintenanceStartUtcKey = "TaxBanditsMaintenanceStartUtc";

    /// <summary>
    /// The key used for storing the Tax Bandits maintenance window end time (UTC).
    /// </summary>
    public const string TaxBanditsMaintenanceEndUtcKey = "TaxBanditsMaintenanceEndUtc";

    /// <summary>
    /// The key used for storing the maximum audio upload file size in MB.
    /// </summary>
    public const string MaxAudioUploadSizeMBKey = "MaxAudioUploadSizeMB";

    /// <summary>
    /// Default maximum audio upload file size in MB if not set in the database.
    /// </summary>
    public const int DefaultMaxAudioUploadSizeMB = 100;

    /// <summary>
    /// The key used for storing the maximum image upload file size in MB.
    /// </summary>
    public const string MaxImageUploadSizeMBKey = "MaxImageUploadSizeMB";

    /// <summary>
    /// Default maximum image upload file size in MB if not set in the database.
    /// </summary>
    public const int DefaultMaxImageUploadSizeMB = 20;

    public AppSettingsService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<AppSettingsService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetSettingAsync(string key)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var setting = await context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key);

        return setting?.Value;
    }

    /// <inheritdoc />
    public async Task SetSettingAsync(string key, string value, string? description = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var setting = await context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key);

        if (setting == null)
        {
            setting = new AppSettings
            {
                Key = key,
                Value = value,
                Description = description,
                UpdatedAt = DateTime.UtcNow
            };
            context.AppSettings.Add(setting);
            _logger.LogInformation("Created new setting: {Key} = {Value}", key, value);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
            if (description != null)
            {
                setting.Description = description;
            }
            _logger.LogInformation("Updated setting: {Key} = {Value}", key, value);
        }

        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<decimal> GetSubscriptionPriceAsync()
    {
        var value = await GetSettingAsync(SubscriptionPriceKey);
        
        if (string.IsNullOrEmpty(value))
        {
            return DefaultSubscriptionPrice;
        }

        if (decimal.TryParse(value, out var price))
        {
            return price;
        }

        _logger.LogWarning("Invalid subscription price value in database: {Value}. Using default.", value);
        return DefaultSubscriptionPrice;
    }

    /// <inheritdoc />
    public async Task SetSubscriptionPriceAsync(decimal price)
    {
        await SetSettingAsync(
            SubscriptionPriceKey,
            price.ToString("F2"),
            "Monthly subscription price in USD");
    }

    /// <summary>
    /// Gets the stream pay rate for creators.
    /// </summary>
    /// <returns>The stream pay rate as a decimal (0.005 = $5 per 1000 streams), or the default value if not set.</returns>
    public async Task<decimal> GetStreamPayRateAsync()
    {
        var value = await GetSettingAsync(StreamPayRateKey);
        
        if (string.IsNullOrEmpty(value))
        {
            return DefaultStreamPayRate;
        }

        if (decimal.TryParse(value, out var rate))
        {
            return rate;
        }

        _logger.LogWarning("Invalid stream pay rate value in database: {Value}. Using default.", value);
        return DefaultStreamPayRate;
    }

    /// <summary>
    /// Sets the stream pay rate for creators.
    /// </summary>
    /// <param name="rate">The stream pay rate as a decimal (0.005 = $5 per 1000 streams).</param>
    public async Task SetStreamPayRateAsync(decimal rate)
    {
        await SetSettingAsync(
            StreamPayRateKey,
            rate.ToString("F6"),
            "Stream pay rate for creators in USD per stream (0.005 = $5 per 1000 streams)");
    }

    /// <inheritdoc />
    public async Task<int> GetStreamQualifyingSecondsAsync()
    {
        var value = await GetSettingAsync(StreamQualifyingSecondsKey);
        
        if (string.IsNullOrEmpty(value))
        {
            return DefaultStreamQualifyingSeconds;
        }

        if (int.TryParse(value, out var seconds))
        {
            return seconds;
        }

        _logger.LogWarning("Invalid stream qualifying seconds value in database: {Value}. Using default.", value);
        return DefaultStreamQualifyingSeconds;
    }

    /// <inheritdoc />
    public async Task SetStreamQualifyingSecondsAsync(int seconds)
    {
        await SetSettingAsync(
            StreamQualifyingSecondsKey,
            seconds.ToString(),
            "Number of continuous seconds of playback that qualifies as a stream");
    }

    /// <inheritdoc />
    public async Task<bool> GetTaxBanditsMaintenanceEnabledAsync()
    {
        var value = await GetSettingAsync(TaxBanditsMaintenanceEnabledKey);
        return bool.TryParse(value, out var enabled) && enabled;
    }

    /// <inheritdoc />
    public async Task SetTaxBanditsMaintenanceEnabledAsync(bool enabled)
    {
        await SetSettingAsync(
            TaxBanditsMaintenanceEnabledKey,
            enabled.ToString(),
            "Whether the Tax Bandits maintenance window warning is enabled");
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetTaxBanditsMaintenanceStartUtcAsync()
    {
        var value = await GetSettingAsync(TaxBanditsMaintenanceStartUtcKey);
        if (string.IsNullOrEmpty(value))
            return null;
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    /// <inheritdoc />
    public async Task SetTaxBanditsMaintenanceStartUtcAsync(DateTime startUtc)
    {
        await SetSettingAsync(
            TaxBanditsMaintenanceStartUtcKey,
            startUtc.ToUniversalTime().ToString("O"),
            "Tax Bandits maintenance window start time (UTC)");
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetTaxBanditsMaintenanceEndUtcAsync()
    {
        var value = await GetSettingAsync(TaxBanditsMaintenanceEndUtcKey);
        if (string.IsNullOrEmpty(value))
            return null;
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    /// <inheritdoc />
    public async Task SetTaxBanditsMaintenanceEndUtcAsync(DateTime endUtc)
    {
        await SetSettingAsync(
            TaxBanditsMaintenanceEndUtcKey,
            endUtc.ToUniversalTime().ToString("O"),
            "Tax Bandits maintenance window end time (UTC)");
    }

    /// <inheritdoc />
    public async Task<int> GetMaxAudioUploadSizeMBAsync()
    {
        var value = await GetSettingAsync(MaxAudioUploadSizeMBKey);

        if (string.IsNullOrEmpty(value))
        {
            return DefaultMaxAudioUploadSizeMB;
        }

        if (int.TryParse(value, out var sizeMB))
        {
            return sizeMB;
        }

        _logger.LogWarning("Invalid max audio upload size value in database: {Value}. Using default.", value);
        return DefaultMaxAudioUploadSizeMB;
    }

    /// <inheritdoc />
    public async Task SetMaxAudioUploadSizeMBAsync(int sizeMB)
    {
        await SetSettingAsync(
            MaxAudioUploadSizeMBKey,
            sizeMB.ToString(),
            "Maximum audio upload file size in MB");
    }

    /// <inheritdoc />
    public async Task<int> GetMaxImageUploadSizeMBAsync()
    {
        var value = await GetSettingAsync(MaxImageUploadSizeMBKey);

        if (string.IsNullOrEmpty(value))
        {
            return DefaultMaxImageUploadSizeMB;
        }

        if (int.TryParse(value, out var sizeMB))
        {
            return sizeMB;
        }

        _logger.LogWarning("Invalid max image upload size value in database: {Value}. Using default.", value);
        return DefaultMaxImageUploadSizeMB;
    }

    /// <inheritdoc />
    public async Task SetMaxImageUploadSizeMBAsync(int sizeMB)
    {
        await SetSettingAsync(
            MaxImageUploadSizeMBKey,
            sizeMB.ToString(),
            "Maximum image upload file size in MB");
    }

    /// <inheritdoc />
    public async Task<bool> IsTaxBanditsMaintenanceActiveAsync()
    {
        var enabled = await GetTaxBanditsMaintenanceEnabledAsync();
        if (!enabled)
            return false;

        var start = await GetTaxBanditsMaintenanceStartUtcAsync();
        var end = await GetTaxBanditsMaintenanceEndUtcAsync();

        if (!start.HasValue || !end.HasValue)
            return false;

        var now = DateTime.UtcNow;
        return now >= start.Value && now <= end.Value;
    }

    /// <inheritdoc />
    public async Task<bool> ShouldShowTaxBanditsMaintenanceWarningAsync()
    {
        var enabled = await GetTaxBanditsMaintenanceEnabledAsync();
        if (!enabled)
            return false;

        var end = await GetTaxBanditsMaintenanceEndUtcAsync();
        if (!end.HasValue)
            return false;

        return end.Value > DateTime.UtcNow;
    }
}
