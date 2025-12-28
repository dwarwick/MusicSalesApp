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
}
