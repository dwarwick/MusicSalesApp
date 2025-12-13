using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SubscriptionService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<Subscription> GetActiveSubscriptionAsync(int userId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var now = DateTime.UtcNow;
        return await context.Subscriptions
            .Where(s => s.UserId == userId)
            .Where(s => (s.Status == "ACTIVE" && (s.EndDate == null || s.EndDate > now)) ||
                                 (s.Status == "CANCELLED" && s.EndDate > now))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> HasActiveSubscriptionAsync(int userId)
    {
        var subscription = await GetActiveSubscriptionAsync(userId);
        
        if (subscription == null)
            return false;

        // Check if subscription is still valid based on EndDate
        if (subscription.EndDate.HasValue && subscription.EndDate.Value <= DateTime.UtcNow)
            return false;

        return true;
    }

    public async Task<Subscription> CreateSubscriptionAsync(int userId, string paypalSubscriptionId, decimal monthlyPrice)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        // Cancel any existing active subscriptions
        var existingSubscriptions = await context.Subscriptions
            .Where(s => s.UserId == userId && s.Status == "ACTIVE")
            .ToListAsync();

        foreach (var existing in existingSubscriptions)
        {
            existing.Status = "CANCELLED";
            existing.CancelledAt = DateTime.UtcNow;
        }

        var subscription = new Subscription
        {
            UserId = userId,
            PayPalSubscriptionId = paypalSubscriptionId,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow,
            MonthlyPrice = monthlyPrice,
            CreatedAt = DateTime.UtcNow,
            // EndDate will be set based on PayPal webhook or cancellation
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created subscription {SubscriptionId} for user {UserId}", subscription.Id, userId);
        
        return subscription;
    }

    public async Task<bool> CancelSubscriptionAsync(int userId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var subscription = await context.Subscriptions
            .Where(s => s.UserId == userId && s.Status == "ACTIVE")
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (subscription == null)
            return false;

        subscription.Status = "CANCELLED";
        subscription.CancelledAt = DateTime.UtcNow;
        
        // Set end date to the next billing date or 30 days from now if not set
        if (!subscription.NextBillingDate.HasValue)
        {
            subscription.EndDate = DateTime.UtcNow.AddDays(30);
        }
        else
        {
            subscription.EndDate = subscription.NextBillingDate.Value;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Cancelled subscription {SubscriptionId} for user {UserId}, valid until {EndDate}", 
            subscription.Id, userId, subscription.EndDate);
        
        return true;
    }

    public async Task<Subscription> GetSubscriptionByPayPalIdAsync(string paypalSubscriptionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        return await context.Subscriptions
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId);
    }

    public async Task UpdateSubscriptionStatusAsync(string paypalSubscriptionId, string status, DateTime? nextBillingDate = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId);

        if (subscription == null)
        {
            _logger.LogWarning("Subscription with PayPal ID {PayPalSubscriptionId} not found", paypalSubscriptionId);
            return;
        }

        subscription.Status = status;
        
        if (nextBillingDate.HasValue)
        {
            subscription.NextBillingDate = nextBillingDate.Value;
        }

        if (status == "CANCELLED" || status == "SUSPENDED" || status == "EXPIRED")
        {
            subscription.CancelledAt = DateTime.UtcNow;
            if (!subscription.EndDate.HasValue && subscription.NextBillingDate.HasValue)
            {
                subscription.EndDate = subscription.NextBillingDate.Value;
            }
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated subscription {SubscriptionId} status to {Status}", subscription.Id, status);
    }

    public async Task UpdateSubscriptionDetailsAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId);

        if (subscription == null)
        {
            _logger.LogWarning("Subscription with PayPal ID {PayPalSubscriptionId} not found", paypalSubscriptionId);
            return;
        }

        if (nextBillingDate.HasValue)
        {
            subscription.NextBillingDate = nextBillingDate.Value;
        }

        if (lastPaymentDate.HasValue)
        {
            subscription.LastPaymentDate = lastPaymentDate.Value;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated subscription {SubscriptionId} billing details", subscription.Id);
    }

    public async Task<bool> DeletePendingSubscriptionAsync(int userId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        // Find the most recent subscription for this user that hasn't been paid for yet
        // (no LastPaymentDate)
        var pendingSubscription = await context.Subscriptions
            .Where(s => s.UserId == userId && 
                       s.Status == "ACTIVE" && 
                       s.LastPaymentDate == null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (pendingSubscription == null)
            return false;

        // Delete the pending subscription
        context.Subscriptions.Remove(pendingSubscription);
        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted pending subscription {SubscriptionId} for user {UserId}", 
            pendingSubscription.Id, userId);
        
        return true;
    }
}
