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
            .Where(s => (s.Status == SubscriptionStatuses.Active &&
                         (s.LastPaymentDate != null || s.TrialEndDate > now) &&
                         (s.EndDate == null || s.EndDate > now)) ||
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

    public async Task<Subscription> GetCurrentSubscriptionFromOtherProviderAsync(
        int userId,
        string billingSource)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await NormalizeExpiredSubscriptionsAsync(context, userId);

        var now = DateTime.UtcNow;
        return await context.Subscriptions
            .Where(subscription => subscription.UserId == userId
                && subscription.BillingSource != billingSource)
            .Where(subscription =>
                (subscription.Status == SubscriptionStatuses.Active
                 && (subscription.LastPaymentDate.HasValue || subscription.TrialEndDate > now)
                 && (!subscription.EndDate.HasValue || subscription.EndDate > now))
                || (subscription.Status == SubscriptionStatuses.Suspended
                    && (!subscription.EndDate.HasValue || subscription.EndDate > now))
                || (subscription.Status == SubscriptionStatuses.Cancelled
                    && subscription.EndDate > now))
            .OrderByDescending(subscription => subscription.CreatedAt)
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

    public async Task<bool> HasPriorActivatedSubscriptionAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Subscriptions
            .Where(s => s.UserId == userId)
            // ActivatedAtUtc rather than "is the row currently ACTIVE or SUSPENDED". Reading the
            // current status made trial eligibility a side effect of any status change: a
            // cancellation - from a webhook, from the entitlement drift sweep, from anything -
            // moved the row to CANCELLED and handed the user a second free trial. Activation is a
            // fact about the past, so it is now recorded as one.
            .AnyAsync(s => s.Status != SubscriptionStatuses.ApprovalPending &&
                           (s.ActivatedAtUtc.HasValue ||
                            s.LastPaymentDate.HasValue ||
                            s.TrialStartDate.HasValue ||
                            s.TrialEndDate.HasValue ||
                            s.TrialConvertedAt.HasValue ||
                            (s.BillingSource == BillingSources.GooglePlay
                             && s.GooglePlayPurchaseToken != null) ||
                            (s.BillingSource == BillingSources.Apple
                             && s.AppStoreOriginalTransactionId != null)));
    }

    public async Task<int> NormalizeExpiredSubscriptionsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await NormalizeExpiredSubscriptionsAsync(context);
    }

    public Task<Subscription> CreateSubscriptionAsync(int userId, string paypalSubscriptionId, decimal monthlyPrice)
    {
        return CreateSubscriptionAsync(
            userId,
            paypalSubscriptionId,
            paypalPlanId: null,
            monthlyPrice,
            priceCurrencyCode: null);
    }

    public Task<Subscription> CreateSubscriptionAsync(
        int userId,
        string paypalSubscriptionId,
        string paypalPlanId,
        decimal monthlyPrice,
        string priceCurrencyCode)
    {
        return CreateSubscriptionAsync(
            userId,
            paypalSubscriptionId,
            paypalPlanId,
            monthlyPrice,
            priceCurrencyCode,
            payPalOfferVersion: null,
            subscriptionTermsAcceptedAt: DateTime.UtcNow);
    }

    public async Task<Subscription> CreateSubscriptionAsync(
        int userId,
        string paypalSubscriptionId,
        string paypalPlanId,
        decimal monthlyPrice,
        string priceCurrencyCode,
        int? payPalOfferVersion,
        DateTime subscriptionTermsAcceptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paypalSubscriptionId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        await NormalizeExpiredSubscriptionsAsync(context, userId);

        if (await HasCurrentEntitlementAsync(context, userId))
        {
            throw new InvalidOperationException("The user already has a current subscription entitlement.");
        }

        // Remove any stale APPROVAL_PENDING subscriptions (user started but never completed)
        var pendingSubscriptions = await context.Subscriptions
            .Where(s => s.UserId == userId &&
                        s.BillingSource == BillingSources.PayPal &&
                        s.Status == SubscriptionStatuses.ApprovalPending)
            .ToListAsync();

        context.Subscriptions.RemoveRange(pendingSubscriptions);

        var subscription = new Subscription
        {
            UserId = userId,
            PayPalSubscriptionId = paypalSubscriptionId,
            PayPalPlanId = NormalizeOptionalValue(paypalPlanId),
            BillingSource = BillingSources.PayPal,
            Status = SubscriptionStatuses.ApprovalPending,
            StartDate = DateTime.UtcNow,
            MonthlyPrice = monthlyPrice,
            PayPalOfferVersion = payPalOfferVersion,
            SubscriptionTermsAcceptedAt = DateTime.SpecifyKind(
                subscriptionTermsAcceptedAt,
                DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            // EndDate will be set based on PayPal webhook or cancellation
        };

        ApplyStorePriceInfo(subscription, null, priceCurrencyCode);

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created subscription {SubscriptionId} for user {UserId}", subscription.Id, userId);
        
        return subscription;
    }

    public Task<bool> CancelSubscriptionAsync(int userId)
    {
        return CancelSubscriptionAsync(userId, providerEntitlementEndDate: null);
    }

    public async Task<bool> CancelSubscriptionAsync(int userId, DateTime? providerEntitlementEndDate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        var subscription = await context.Subscriptions
            .Where(s => s.UserId == userId &&
                        (s.Status == SubscriptionStatuses.Active ||
                         s.Status == SubscriptionStatuses.ApprovalPending ||
                         s.Status == SubscriptionStatuses.Cancelled ||
                         s.Status == SubscriptionStatuses.Expired))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (subscription == null)
            return false;

        subscription.Status = SubscriptionStatuses.Cancelled;
        subscription.CancelledAt ??= DateTime.UtcNow;

        // The provider-confirmed paid-through/trial end is authoritative. For legacy
        // callers, retain only dates already confirmed and stored; never invent access.
        subscription.EndDate = providerEntitlementEndDate ?? GetBestKnownEntitlementEnd(subscription);

        await context.SaveChangesAsync();

        _logger.LogInformation("Cancelled subscription {SubscriptionId} for user {UserId}, valid until {EndDate}",
            subscription.Id, userId, subscription.EndDate);
        
        return true;
    }

    public async Task<bool> CancelPayPalSubscriptionAsync(
        string paypalSubscriptionId,
        DateTime? providerEntitlementEndDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paypalSubscriptionId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s =>
                s.BillingSource == BillingSources.PayPal
                && s.PayPalSubscriptionId == paypalSubscriptionId);

        if (subscription == null)
        {
            return false;
        }

        subscription.Status = SubscriptionStatuses.Cancelled;
        subscription.CancelledAt ??= DateTime.UtcNow;
        // This exact-row path is only called after provider reconciliation. A null
        // end means PayPal confirmed no activated entitlement (for example, an
        // abandoned approval), so stale local dates must not manufacture access.
        subscription.EndDate = providerEntitlementEndDate;

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Cancelled PayPal subscription {SubscriptionId}, valid until {EndDate}",
            subscription.Id,
            subscription.EndDate);
        return true;
    }

    public async Task<PayPalSubscriptionReconciliationResult> ReconcilePayPalSubscriptionAsync(
        string paypalSubscriptionId,
        PayPalSubscriptionReconciliation reconciliation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paypalSubscriptionId);
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliation.Status);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId);

        if (subscription == null)
        {
            _logger.LogWarning(
                "Subscription with PayPal ID {PayPalSubscriptionId} not found for reconciliation",
                paypalSubscriptionId);
            return null;
        }

        var reconciledAt = DateTime.UtcNow;
        var previousStatus = subscription.Status;
        var previousLastPaymentDate = subscription.LastPaymentDate;
        var previousTrialConvertedAt = subscription.TrialConvertedAt;

        var normalizedStatus = reconciliation.Status.Trim().ToUpperInvariant();
        var resolvedStatus = normalizedStatus == SubscriptionStatuses.Canceled
            ? SubscriptionStatuses.Cancelled
            : normalizedStatus;

        // PayPal keeps reporting APPROVAL_PENDING for a few minutes after accepting the
        // cancellation of an agreement the buyer never approved. Any status refresh landing in that
        // window would otherwise overwrite the CANCELLED we just wrote, leaving the row stuck at
        // APPROVAL_PENDING with CancelledAt set - a state nothing later corrects, because PayPal
        // sends no lifecycle webhook for an agreement that was never approved. An agreement we
        // cancelled cannot legitimately return to unapproved; a genuine reactivation arrives as
        // ACTIVE, which clears CancelledAt just below.
        if (subscription.CancelledAt.HasValue
            && (resolvedStatus == SubscriptionStatuses.ApprovalPending
                || resolvedStatus == SubscriptionStatuses.Approved))
        {
            resolvedStatus = SubscriptionStatuses.Cancelled;
        }

        subscription.Status = resolvedStatus;

        if (subscription.Status == SubscriptionStatuses.Active)
        {
            // A missed renewal webhook can temporarily normalize the local row to
            // EXPIRED. Provider reconciliation is authoritative and reactivates it.
            subscription.CancelledAt = null;
        }

        if (!string.IsNullOrWhiteSpace(reconciliation.PlanId))
        {
            subscription.PayPalPlanId = reconciliation.PlanId.Trim();
        }

        if (reconciliation.ProviderStartDate.HasValue)
        {
            subscription.StartDate = reconciliation.ProviderStartDate.Value;
        }

        if (reconciliation.TrialStartDate.HasValue)
        {
            subscription.TrialStartDate = reconciliation.TrialStartDate.Value;
        }

        if (reconciliation.TrialEndDate.HasValue)
        {
            subscription.TrialEndDate = reconciliation.TrialEndDate.Value;
        }

        if (reconciliation.LastPaymentDate.HasValue &&
            (!subscription.LastPaymentDate.HasValue ||
             reconciliation.LastPaymentDate.Value > subscription.LastPaymentDate.Value))
        {
            subscription.LastPaymentDate = reconciliation.LastPaymentDate.Value;
        }

        if (reconciliation.NextBillingDate.HasValue)
        {
            subscription.NextBillingDate = reconciliation.NextBillingDate.Value;
        }

        if (reconciliation.MonthlyPrice.HasValue)
        {
            subscription.MonthlyPrice = reconciliation.MonthlyPrice.Value;
        }

        ApplyStorePriceInfo(subscription, null, reconciliation.CurrencyCode);

        var hasTrial = subscription.TrialStartDate.HasValue || subscription.TrialEndDate.HasValue;
        if (hasTrial && subscription.LastPaymentDate.HasValue && !subscription.TrialConvertedAt.HasValue)
        {
            subscription.TrialConvertedAt = subscription.LastPaymentDate.Value;
        }

        ApplyPayPalEntitlementEnd(subscription, reconciliation, reconciledAt);

        await context.SaveChangesAsync();

        var becameActive = previousStatus != SubscriptionStatuses.Active &&
                           subscription.Status == SubscriptionStatuses.Active;
        var becamePaid = !previousLastPaymentDate.HasValue && subscription.LastPaymentDate.HasValue;
        var trialConverted = !previousTrialConvertedAt.HasValue && subscription.TrialConvertedAt.HasValue;
        var activeTrial = subscription.Status == SubscriptionStatuses.Active &&
                          !subscription.LastPaymentDate.HasValue &&
                          subscription.TrialEndDate > reconciledAt;

        _logger.LogInformation(
            "Reconciled PayPal subscription {SubscriptionId}: Status {PreviousStatus}->{Status}, PlanId={PlanId}, TrialEndDate={TrialEndDate}, LastPaymentDate={LastPaymentDate}, NextBillingDate={NextBillingDate}, EndDate={EndDate}, TrialConverted={TrialConverted}",
            subscription.Id,
            previousStatus,
            subscription.Status,
            subscription.PayPalPlanId,
            subscription.TrialEndDate,
            subscription.LastPaymentDate,
            subscription.NextBillingDate,
            subscription.EndDate,
            trialConverted);

        return new PayPalSubscriptionReconciliationResult
        {
            Subscription = subscription,
            PreviousStatus = previousStatus,
            PreviousLastPaymentDate = previousLastPaymentDate,
            BecameActive = becameActive,
            BecamePaid = becamePaid,
            TrialConverted = trialConverted,
            ShouldSendTrialActivationEmail = activeTrial && !subscription.TrialActivationEmailSentAt.HasValue,
            ShouldSendTrialConversionEmail = subscription.TrialConvertedAt.HasValue &&
                                             !subscription.TrialConversionEmailSentAt.HasValue
        };
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
        
        // Only PayPal setup records are pending in this cleanup path. Google Play
        // free trials are ACTIVE with no LastPaymentDate until conversion and must not be deleted here.
        var pendingSubscription = await context.Subscriptions
            .Where(s => s.UserId == userId && 
                       s.BillingSource == BillingSources.PayPal &&
                       s.PayPalSubscriptionId != null &&
                       s.Status == SubscriptionStatuses.ApprovalPending)
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

    public async Task<Subscription> CreateGooglePlaySubscriptionAsync(int userId, string purchaseToken, string orderId, decimal monthlyPrice, GooglePlaySubscriptionInfo googlePlayInfo = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        await PrepareForStoreSubscriptionCreationAsync(context, userId, BillingSources.GooglePlay);
        var now = DateTime.UtcNow;
        var expiryTime = googlePlayInfo?.ExpiryTime?.UtcDateTime;

        var subscription = new Subscription
        {
            UserId = userId,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = purchaseToken,
            GooglePlayOrderId = orderId,
            Status = SubscriptionStatuses.Active,
            StartDate = googlePlayInfo?.StartTime?.UtcDateTime ?? now,
            LastPaymentDate = googlePlayInfo?.IsFreeTrial == true ? null : now,
            EndDate = expiryTime,
            NextBillingDate = expiryTime,
            MonthlyPrice = monthlyPrice,
            CreatedAt = now,
        };

        ApplyGooglePlayTrialInfo(subscription, googlePlayInfo, expiryTime, now);
        ApplyStorePriceInfo(subscription, null, googlePlayInfo?.PriceCurrencyCode);

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created Google Play subscription {SubscriptionId} for user {UserId}, order {OrderId}",
            subscription.Id, userId, orderId);

        return subscription;
    }

    public async Task<Subscription> CreateAppleSubscriptionAsync(
        int userId,
        string transactionId,
        string originalTransactionId,
        string productId,
        string appAccountToken,
        string environment,
        decimal monthlyPrice,
        DateTime? startDate = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        await PrepareForStoreSubscriptionCreationAsync(context, userId, BillingSources.Apple);

        var effectiveStartDate = startDate ?? DateTime.UtcNow;

        var subscription = new Subscription
        {
            UserId = userId,
            BillingSource = BillingSources.Apple,
            AppStoreTransactionId = transactionId,
            AppStoreOriginalTransactionId = originalTransactionId,
            AppStoreProductId = productId,
            AppStoreAppAccountToken = appAccountToken,
            AppStoreEnvironment = environment,
            Status = SubscriptionStatuses.Active,
            StartDate = effectiveStartDate,
            LastPaymentDate = effectiveStartDate,
            MonthlyPrice = monthlyPrice,
            CreatedAt = DateTime.UtcNow,
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Created Apple subscription {SubscriptionId} for user {UserId}, original transaction {OriginalTransactionId}",
            subscription.Id,
            userId,
            originalTransactionId);

        return subscription;
    }

    public async Task<Subscription> GetSubscriptionByGooglePlayTokenAsync(string purchaseToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Subscriptions
            .FirstOrDefaultAsync(s => s.GooglePlayPurchaseToken == purchaseToken);
    }

    public async Task UpdateGooglePlaySubscriptionStatusAsync(string purchaseToken, string status, DateTime? expiryTime = null, GooglePlaySubscriptionInfo googlePlayInfo = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.GooglePlayPurchaseToken == purchaseToken);

        if (subscription == null)
        {
            _logger.LogWarning("Google Play subscription with token {PurchaseToken} not found", purchaseToken[..Math.Min(20, purchaseToken.Length)]);
            return;
        }

        if (status == SubscriptionStatuses.Active)
        {
            await EnsureNoCurrentOtherProviderSubscriptionAsync(
                context,
                subscription.UserId,
                BillingSources.GooglePlay,
                subscription.Id);
        }

        var now = DateTime.UtcNow;
        var previousStatus = subscription.Status;
        var previousEndDate = subscription.EndDate;
        var previousLastPaymentDate = subscription.LastPaymentDate;
        var previousMonthlyPrice = subscription.MonthlyPrice;
        subscription.Status = status;

        if (!string.IsNullOrWhiteSpace(googlePlayInfo?.OrderId))
        {
            subscription.GooglePlayOrderId = googlePlayInfo.OrderId;
        }

        if (googlePlayInfo?.RecurringPrice.HasValue == true)
        {
            subscription.MonthlyPrice = googlePlayInfo.RecurringPrice.Value;
        }

        ApplyStorePriceInfo(subscription, null, googlePlayInfo?.PriceCurrencyCode);

        ApplyGooglePlayTrialInfo(subscription, googlePlayInfo, expiryTime, now);

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
            if (googlePlayInfo?.IsFreeTrial == true)
            {
                subscription.LastPaymentDate = null;
            }
            else
            {
                subscription.LastPaymentDate = now;
            }
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Updated Google Play subscription {SubscriptionId}: Status {PreviousStatus}->{Status}, EndDate {PreviousEndDate}->{EndDate}, LastPaymentDate {PreviousLastPaymentDate}->{LastPaymentDate}, MonthlyPrice {PreviousMonthlyPrice}->{MonthlyPrice}, IsFreeTrial={IsFreeTrial}, TrialEndDate={TrialEndDate}, TrialConvertedAt={TrialConvertedAt}, AutoRenewEnabled={AutoRenewEnabled}, GoogleState={GoogleState}",
            subscription.Id,
            previousStatus,
            subscription.Status,
            previousEndDate,
            subscription.EndDate,
            previousLastPaymentDate,
            subscription.LastPaymentDate,
            previousMonthlyPrice,
            subscription.MonthlyPrice,
            googlePlayInfo?.IsFreeTrial,
            subscription.TrialEndDate,
            subscription.TrialConvertedAt,
            subscription.GooglePlayAutoRenewEnabled,
            googlePlayInfo?.SubscriptionState);
    }

    public async Task UpdateGooglePlayStorePriceAsync(string purchaseToken, string formattedPrice, string priceCurrencyCode)
    {
        if (string.IsNullOrWhiteSpace(formattedPrice) && string.IsNullOrWhiteSpace(priceCurrencyCode))
        {
            return;
        }

        using var context = await _contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.GooglePlayPurchaseToken == purchaseToken);

        if (subscription == null)
        {
            _logger.LogWarning("Google Play subscription with token {PurchaseToken} not found while updating store price", purchaseToken[..Math.Min(20, purchaseToken.Length)]);
            return;
        }

        ApplyStorePriceInfo(subscription, formattedPrice, priceCurrencyCode);
        await context.SaveChangesAsync();
    }

    public async Task MarkTrialActivationEmailSentAsync(int subscriptionId)
    {
        await MarkTrialEmailSentAsync(subscriptionId, isConversionEmail: false);
    }

    public async Task MarkTrialConversionEmailSentAsync(int subscriptionId)
    {
        await MarkTrialEmailSentAsync(subscriptionId, isConversionEmail: true);
    }

    public async Task<Subscription> GetSubscriptionByAppleTransactionIdAsync(string transactionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Subscriptions
            .FirstOrDefaultAsync(s => s.AppStoreTransactionId == transactionId);
    }

    public async Task<Subscription> GetSubscriptionByAppleOriginalTransactionIdAsync(string originalTransactionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Subscriptions
            .FirstOrDefaultAsync(s => s.AppStoreOriginalTransactionId == originalTransactionId);
    }

    public async Task UpdateAppleStorePriceAsync(
        string originalTransactionId,
        decimal monthlyPrice,
        string priceCurrencyCode)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.AppStoreOriginalTransactionId == originalTransactionId);
        if (subscription == null)
        {
            return;
        }

        if (monthlyPrice > 0)
        {
            subscription.MonthlyPrice = monthlyPrice;
        }

        ApplyStorePriceInfo(subscription, null, priceCurrencyCode);
        await context.SaveChangesAsync();
    }

    public async Task UpdateAppleSubscriptionStatusAsync(
        string originalTransactionId,
        string status,
        DateTime? expiryTime = null,
        string latestTransactionId = null,
        string environment = null,
        string productId = null,
        string appAccountToken = null,
        decimal? monthlyPrice = null)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.AppStoreOriginalTransactionId == originalTransactionId);

        if (subscription == null && !string.IsNullOrWhiteSpace(latestTransactionId))
        {
            subscription = await context.Subscriptions
                .FirstOrDefaultAsync(s => s.AppStoreTransactionId == latestTransactionId);
        }

        if (subscription == null)
        {
            subscription = await TryCreateAppleSubscriptionFromNotificationAsync(
                context,
                originalTransactionId,
                status,
                expiryTime,
                latestTransactionId,
                environment,
                productId,
                appAccountToken,
                monthlyPrice);

            if (subscription == null)
            {
                _logger.LogWarning(
                    "Apple subscription with original transaction ID {OriginalTransactionId} not found and notification could not be reconciled. AppAccountTokenPresent={AppAccountTokenPresent}",
                    originalTransactionId,
                    !string.IsNullOrWhiteSpace(appAccountToken));
                return;
            }
        }

        if (status == SubscriptionStatuses.Active)
        {
            await EnsureNoCurrentOtherProviderSubscriptionAsync(
                context,
                subscription.UserId,
                BillingSources.Apple,
                subscription.Id);
        }

        subscription.Status = status;

        if (!string.IsNullOrWhiteSpace(latestTransactionId))
        {
            subscription.AppStoreTransactionId = latestTransactionId;
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            subscription.AppStoreEnvironment = environment;
        }

        if (expiryTime.HasValue)
        {
            subscription.EndDate = expiryTime.Value;
            subscription.NextBillingDate = expiryTime.Value;
        }

        if (monthlyPrice.HasValue && monthlyPrice.Value > 0)
        {
            subscription.MonthlyPrice = monthlyPrice.Value;
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

        _logger.LogInformation("Updated Apple subscription {SubscriptionId} status to {Status}", subscription.Id, status);
    }

    private async Task<Subscription> TryCreateAppleSubscriptionFromNotificationAsync(
        AppDbContext context,
        string originalTransactionId,
        string status,
        DateTime? expiryTime,
        string latestTransactionId,
        string environment,
        string productId,
        string appAccountToken,
        decimal? monthlyPrice)
    {
        if (string.IsNullOrWhiteSpace(appAccountToken) || !int.TryParse(appAccountToken, out var userId))
        {
            return null;
        }

        if (!monthlyPrice.HasValue || monthlyPrice.Value <= 0)
        {
            _logger.LogWarning(
                "Apple notification for original transaction {OriginalTransactionId} could not create a local subscription because Apple did not provide a price",
                originalTransactionId);
            return null;
        }

        try
        {
            await PrepareForStoreSubscriptionCreationAsync(context, userId, BillingSources.Apple);
        }
        catch (SubscriptionProviderConflictException ex)
        {
            _logger.LogWarning(
                "Apple notification reconciliation for original transaction {OriginalTransactionId} was rejected because user {UserId} has a current {ExistingBillingSource} subscription",
                originalTransactionId,
                userId,
                ex.ExistingBillingSource);
            return null;
        }

        var effectiveNow = DateTime.UtcNow;
        var effectivePrice = monthlyPrice.Value;
        var subscription = new Subscription
        {
            UserId = userId,
            BillingSource = BillingSources.Apple,
            AppStoreTransactionId = latestTransactionId,
            AppStoreOriginalTransactionId = originalTransactionId,
            AppStoreProductId = productId,
            AppStoreAppAccountToken = appAccountToken,
            AppStoreEnvironment = environment,
            Status = status,
            StartDate = effectiveNow,
            CreatedAt = effectiveNow,
            MonthlyPrice = effectivePrice,
            LastPaymentDate = status == SubscriptionStatuses.Active ? effectiveNow : null,
            EndDate = expiryTime,
            NextBillingDate = expiryTime,
            CancelledAt = status is SubscriptionStatuses.Cancelled or SubscriptionStatuses.Expired or SubscriptionStatuses.Suspended
                ? effectiveNow
                : null
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Created Apple subscription {SubscriptionId} from notification reconciliation for user {UserId}, original transaction {OriginalTransactionId}",
            subscription.Id,
            userId,
            originalTransactionId);

        return subscription;
    }

    private async Task PrepareForStoreSubscriptionCreationAsync(
        AppDbContext context,
        int userId,
        string requestedBillingSource)
    {
        await NormalizeExpiredSubscriptionsAsync(context, userId);

        var now = DateTime.UtcNow;
        var currentSubscriptions = await context.Subscriptions
            .Where(s => s.UserId == userId)
            .Where(s => ((s.Status == SubscriptionStatuses.Active ||
                         s.Status == SubscriptionStatuses.Suspended) &&
                        (!s.EndDate.HasValue || s.EndDate > now)) ||
                       (s.Status == SubscriptionStatuses.Cancelled && s.EndDate > now))
            .ToListAsync();

        var conflictingSubscription = currentSubscriptions.FirstOrDefault(subscription =>
            !string.Equals(
                subscription.BillingSource,
                requestedBillingSource,
                StringComparison.OrdinalIgnoreCase));
        if (conflictingSubscription != null)
        {
            throw new SubscriptionProviderConflictException(
                NormalizeOptionalValue(conflictingSubscription.BillingSource) ?? "another provider",
                requestedBillingSource);
        }

        // A provider-verified replacement within the same store supersedes the
        // previous local record. Cross-provider records are never changed here.
        foreach (var existing in currentSubscriptions.Where(subscription =>
                     subscription.Status == SubscriptionStatuses.Active ||
                     subscription.Status == SubscriptionStatuses.Suspended))
        {
            existing.Status = SubscriptionStatuses.Cancelled;
            existing.CancelledAt ??= now;
        }
    }

    private static async Task EnsureNoCurrentOtherProviderSubscriptionAsync(
        AppDbContext context,
        int userId,
        string requestedBillingSource,
        int excludedSubscriptionId)
    {
        var now = DateTime.UtcNow;
        var conflictingSubscription = await context.Subscriptions
            .Where(subscription => subscription.UserId == userId
                && subscription.Id != excludedSubscriptionId
                && subscription.BillingSource != requestedBillingSource)
            .Where(subscription =>
                ((subscription.Status == SubscriptionStatuses.Active
                  || subscription.Status == SubscriptionStatuses.Suspended)
                 && (!subscription.EndDate.HasValue || subscription.EndDate > now))
                || (subscription.Status == SubscriptionStatuses.Cancelled
                    && subscription.EndDate > now))
            .OrderByDescending(subscription => subscription.CreatedAt)
            .FirstOrDefaultAsync();

        if (conflictingSubscription != null)
        {
            throw new SubscriptionProviderConflictException(
                NormalizeOptionalValue(conflictingSubscription.BillingSource) ?? "another provider",
                requestedBillingSource);
        }
    }

    private static void ApplyGooglePlayTrialInfo(Subscription subscription, GooglePlaySubscriptionInfo googlePlayInfo, DateTime? expiryTime, DateTime now)
    {
        if (googlePlayInfo == null)
        {
            return;
        }

        subscription.GooglePlayAutoRenewEnabled = googlePlayInfo.AutoRenewEnabled;

        if (googlePlayInfo.IsFreeTrial)
        {
            subscription.TrialStartDate ??= googlePlayInfo.StartTime?.UtcDateTime ?? subscription.StartDate;
            subscription.TrialEndDate = expiryTime ?? googlePlayInfo.ExpiryTime?.UtcDateTime;
            subscription.TrialBasePlanId = googlePlayInfo.BasePlanId;
            subscription.TrialOfferId = googlePlayInfo.OfferId;
            subscription.TrialOfferTags = googlePlayInfo.OfferTags?.Count > 0
                ? string.Join(",", googlePlayInfo.OfferTags)
                : null;
            return;
        }

        if (subscription.TrialEndDate.HasValue && !subscription.TrialConvertedAt.HasValue)
        {
            subscription.TrialConvertedAt = now;
        }
    }

    private static void ApplyStorePriceInfo(Subscription subscription, string formattedPrice, string priceCurrencyCode)
    {
        if (!string.IsNullOrWhiteSpace(formattedPrice))
        {
            subscription.StoreFormattedPrice = formattedPrice.Trim();
        }

        if (!string.IsNullOrWhiteSpace(priceCurrencyCode))
        {
            subscription.StorePriceCurrencyCode = priceCurrencyCode.Trim().ToUpperInvariant();
        }
    }

    private static void ApplyPayPalEntitlementEnd(
        Subscription subscription,
        PayPalSubscriptionReconciliation reconciliation,
        DateTime reconciledAt)
    {
        if (subscription.Status == SubscriptionStatuses.Active)
        {
            if (subscription.LastPaymentDate.HasValue && subscription.NextBillingDate.HasValue)
            {
                subscription.EndDate = reconciliation.FailedPaymentsCount > 0
                    ? ResolveConfirmedPaidThrough(subscription, reconciliation)
                    : subscription.NextBillingDate.Value;
            }
            else if (!subscription.LastPaymentDate.HasValue && subscription.TrialEndDate.HasValue)
            {
                subscription.EndDate = subscription.TrialEndDate.Value;
            }

            return;
        }

        if (subscription.Status == SubscriptionStatuses.Cancelled)
        {
            subscription.CancelledAt ??= reconciledAt;

            var isUnconvertedTrial = !subscription.LastPaymentDate.HasValue
                && (reconciliation.TrialEndDate.HasValue || subscription.TrialEndDate.HasValue);
            if (isUnconvertedTrial)
            {
                // A retry timestamp after a failed first charge must never extend a
                // cancelled free trial beyond its provider-confirmed trial end.
                subscription.EndDate = reconciliation.TrialEndDate
                    ?? subscription.TrialEndDate
                    ?? subscription.EndDate;
            }
            else
            {
                subscription.EndDate = reconciliation.FailedPaymentsCount > 0
                    ? ResolveConfirmedPaidThrough(subscription, reconciliation)
                    : reconciliation.NextBillingDate ?? GetBestKnownEntitlementEnd(subscription);
            }

            return;
        }

        if (subscription.Status == SubscriptionStatuses.Suspended ||
            subscription.Status == SubscriptionStatuses.Expired)
        {
            subscription.CancelledAt ??= reconciledAt;
        }
    }

    private static DateTime? ResolveConfirmedPaidThrough(
        Subscription subscription,
        PayPalSubscriptionReconciliation reconciliation)
    {
        if (subscription.EndDate.HasValue
            && (!subscription.LastPaymentDate.HasValue
                || subscription.EndDate.Value > subscription.LastPaymentDate.Value))
        {
            return subscription.EndDate;
        }

        if (!subscription.LastPaymentDate.HasValue)
        {
            return subscription.TrialEndDate ?? subscription.EndDate;
        }

        var intervalCount = Math.Max(reconciliation.RegularIntervalCount, 1);
        return reconciliation.RegularIntervalUnit?.ToUpperInvariant() switch
        {
            PayPalBillingIntervals.Day => subscription.LastPaymentDate.Value.AddDays(intervalCount),
            PayPalBillingIntervals.Week => subscription.LastPaymentDate.Value.AddDays(7 * intervalCount),
            PayPalBillingIntervals.Month => subscription.LastPaymentDate.Value.AddMonths(intervalCount),
            PayPalBillingIntervals.Year => subscription.LastPaymentDate.Value.AddYears(intervalCount),
            _ => subscription.EndDate
        };
    }

    private static DateTime? GetBestKnownEntitlementEnd(Subscription subscription)
    {
        return LatestDate(
            subscription.TrialEndDate,
            subscription.NextBillingDate,
            subscription.EndDate);
    }

    private static DateTime? LatestDate(params DateTime?[] values)
    {
        return values
            .Where(value => value.HasValue)
            .Select(value => value.Value)
            .OrderByDescending(value => value)
            .Cast<DateTime?>()
            .FirstOrDefault();
    }

    private static async Task<bool> HasCurrentEntitlementAsync(AppDbContext context, int userId)
    {
        var now = DateTime.UtcNow;
        return await context.Subscriptions
            .Where(s => s.UserId == userId)
            .AnyAsync(s => (s.Status == SubscriptionStatuses.Active &&
                            (s.LastPaymentDate.HasValue || s.TrialEndDate > now) &&
                            (!s.EndDate.HasValue || s.EndDate > now)) ||
                           (s.Status == SubscriptionStatuses.Cancelled && s.EndDate > now));
    }

    private static string NormalizeOptionalValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task MarkTrialEmailSentAsync(int subscriptionId, bool isConversionEmail)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var subscription = await context.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (subscription == null)
        {
            _logger.LogWarning("Subscription {SubscriptionId} not found while marking trial email as sent", subscriptionId);
            return;
        }

        if (isConversionEmail)
        {
            subscription.TrialConversionEmailSentAt ??= DateTime.UtcNow;
        }
        else
        {
            subscription.TrialActivationEmailSentAt ??= DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    private async Task NormalizeExpiredSubscriptionsAsync(AppDbContext context, int userId)
    {
        await NormalizeExpiredSubscriptionsAsync(context, query => query.Where(s => s.UserId == userId));
    }

    private async Task<int> NormalizeExpiredSubscriptionsAsync(AppDbContext context)
    {
        return await NormalizeExpiredSubscriptionsAsync(context, query => query);
    }

    private async Task<int> NormalizeExpiredSubscriptionsAsync(
        AppDbContext context,
        Func<IQueryable<Subscription>, IQueryable<Subscription>> filter)
    {
        var now = DateTime.UtcNow;
        var subscriptionsToExpire = await filter(context.Subscriptions)
            .Where(s => s.EndDate.HasValue && s.EndDate.Value <= now)
            .Where(s => s.Status == SubscriptionStatuses.Active ||
                        s.Status == SubscriptionStatuses.Cancelled ||
                        s.Status == SubscriptionStatuses.Suspended)
            .ToListAsync();

        if (subscriptionsToExpire.Count == 0)
        {
            return 0;
        }

        foreach (var subscription in subscriptionsToExpire)
        {
            _logger.LogInformation(
                "Normalizing subscription {SubscriptionId} for user {UserId} to EXPIRED: PreviousStatus={PreviousStatus}, BillingSource={BillingSource}, EndDate={EndDate}, TrialEndDate={TrialEndDate}, LastPaymentDate={LastPaymentDate}, AutoRenewEnabled={AutoRenewEnabled}",
                subscription.Id,
                subscription.UserId,
                subscription.Status,
                subscription.BillingSource,
                subscription.EndDate,
                subscription.TrialEndDate,
                subscription.LastPaymentDate,
                subscription.GooglePlayAutoRenewEnabled);

            subscription.Status = SubscriptionStatuses.Expired;
            subscription.CancelledAt ??= subscription.EndDate;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Normalized {Count} expired subscriptions to {Status}",
            subscriptionsToExpire.Count,
            SubscriptionStatuses.Expired);

        return subscriptionsToExpire.Count;
    }
}
