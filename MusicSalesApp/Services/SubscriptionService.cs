using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
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
        await NormalizeExpiredSubscriptionsAsync(context, userId);

        var now = DateTime.UtcNow;
        return await context.Subscriptions
            .Where(s => s.UserId == userId)
            .Where(s => (s.Status == SubscriptionStatuses.Active && s.LastPaymentDate != null && (s.EndDate == null || s.EndDate > now)) ||
                                 (s.Status == SubscriptionStatuses.Cancelled && s.EndDate > now))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<Subscription> GetLatestSubscriptionAsync(int userId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        await NormalizeExpiredSubscriptionsAsync(context, userId);

        return await context.Subscriptions
            .Where(s => s.UserId == userId)
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
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatuses.Active)
            .ToListAsync();

        foreach (var existing in existingSubscriptions)
        {
            existing.Status = SubscriptionStatuses.Cancelled;
            existing.CancelledAt = DateTime.UtcNow;
        }

        // Remove any stale APPROVAL_PENDING subscriptions (user started but never completed)
        var pendingSubscriptions = await context.Subscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatuses.ApprovalPending)
            .ToListAsync();

        context.Subscriptions.RemoveRange(pendingSubscriptions);

        var subscription = new Subscription
        {
            UserId = userId,
            PayPalSubscriptionId = paypalSubscriptionId,
            Status = SubscriptionStatuses.ApprovalPending,
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
            .Where(s => s.UserId == userId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.ApprovalPending))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (subscription == null)
            return false;

        subscription.Status = SubscriptionStatuses.Cancelled;
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

        if (status == SubscriptionStatuses.Cancelled || status == SubscriptionStatuses.Suspended || status == SubscriptionStatuses.Expired)
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
        // (APPROVAL_PENDING status or ACTIVE with no LastPaymentDate)
        var pendingSubscription = await context.Subscriptions
            .Where(s => s.UserId == userId && 
                       (s.Status == SubscriptionStatuses.ApprovalPending ||
                        (s.Status == SubscriptionStatuses.Active && s.LastPaymentDate == null)))
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

    public async Task<Subscription> GetPendingSubscriptionAsync(int userId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        return await context.Subscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatuses.ApprovalPending)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task ActivateSubscriptionAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId);

        if (subscription == null)
        {
            _logger.LogWarning("Subscription with PayPal ID {PayPalSubscriptionId} not found for activation", paypalSubscriptionId);
            return;
        }

        subscription.Status = SubscriptionStatuses.Active;
        subscription.StartDate = DateTime.UtcNow;

        if (nextBillingDate.HasValue)
        {
            subscription.NextBillingDate = nextBillingDate.Value;
        }

        if (lastPaymentDate.HasValue)
        {
            subscription.LastPaymentDate = lastPaymentDate.Value;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Activated subscription {SubscriptionId} for user {UserId}", subscription.Id, subscription.UserId);
    }

    // --- Google Play billing methods ---

    public async Task<Subscription> CreateGooglePlaySubscriptionAsync(int userId, string purchaseToken, string orderId, decimal monthlyPrice)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        // Cancel any existing active subscriptions for this user
        var existingSubscriptions = await context.Subscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatuses.Active)
            .ToListAsync();

        foreach (var existing in existingSubscriptions)
        {
            existing.Status = SubscriptionStatuses.Cancelled;
            existing.CancelledAt = DateTime.UtcNow;
        }

        var subscription = new Subscription
        {
            UserId = userId,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = purchaseToken,
            GooglePlayOrderId = orderId,
            Status = SubscriptionStatuses.Active,
            StartDate = DateTime.UtcNow,
            LastPaymentDate = DateTime.UtcNow,
            MonthlyPrice = monthlyPrice,
            CreatedAt = DateTime.UtcNow,
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created Google Play subscription {SubscriptionId} for user {UserId}, order {OrderId}",
            subscription.Id, userId, orderId);

        return subscription;
    }

    public async Task<Subscription> GetSubscriptionByGooglePlayTokenAsync(string purchaseToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Subscriptions
            .FirstOrDefaultAsync(s => s.GooglePlayPurchaseToken == purchaseToken);
    }

    public async Task UpdateGooglePlaySubscriptionStatusAsync(string purchaseToken, string status, DateTime? expiryTime = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.GooglePlayPurchaseToken == purchaseToken);

        if (subscription == null)
        {
            _logger.LogWarning("Google Play subscription with token {PurchaseToken} not found", purchaseToken[..Math.Min(20, purchaseToken.Length)]);
            return;
        }

        subscription.Status = status;

        if (expiryTime.HasValue)
        {
            subscription.EndDate = expiryTime.Value;
            subscription.NextBillingDate = expiryTime.Value;
        }

        if (status == SubscriptionStatuses.Cancelled || status == SubscriptionStatuses.Suspended || status == SubscriptionStatuses.Expired)
        {
            subscription.CancelledAt ??= DateTime.UtcNow;
        }

        if (status == SubscriptionStatuses.Active)
        {
            subscription.LastPaymentDate = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated Google Play subscription {SubscriptionId} status to {Status}", subscription.Id, status);
    }

    private async Task NormalizeExpiredSubscriptionsAsync(AppDbContext context, int userId)
    {
        var now = DateTime.UtcNow;
        var subscriptionsToExpire = await context.Subscriptions
            .Where(s => s.UserId == userId)
            .Where(s => s.EndDate.HasValue && s.EndDate.Value <= now)
            .Where(s => s.Status == SubscriptionStatuses.Active ||
                        s.Status == SubscriptionStatuses.Cancelled ||
                        s.Status == SubscriptionStatuses.Suspended)
            .ToListAsync();

        if (subscriptionsToExpire.Count == 0)
        {
            return;
        }

        foreach (var subscription in subscriptionsToExpire)
        {
            subscription.Status = SubscriptionStatuses.Expired;
            subscription.CancelledAt ??= subscription.EndDate;
        }

        await context.SaveChangesAsync();
    }
}
