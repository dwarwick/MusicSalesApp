using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

#nullable enable

namespace MusicSalesApp.Services;

public sealed class PayPalSubscriptionAnomalyService : IPayPalSubscriptionAnomalyService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> EpisodeLocks = new();

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PayPalSubscriptionAnomalyService> _logger;

    public PayPalSubscriptionAnomalyService(
        IDbContextFactory<AppDbContext> contextFactory,
        IEmailService emailService,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<PayPalSubscriptionAnomalyService> logger)
    {
        _contextFactory = contextFactory;
        _emailService = emailService;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PayPalSubscriptionAnomaly?> GetOpenEpisodeAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PayPalSubscriptionAnomalies
            .AsNoTracking()
            .SingleOrDefaultAsync(
                anomaly => anomaly.SubscriptionId == subscriptionId && anomaly.ResolvedAtUtc == null,
                cancellationToken);
    }

    public async Task<PayPalSubscriptionAnomaly> RecordMismatchAsync(
        Subscription subscription,
        ApplicationUser user,
        PayPalSubscriptionDetails providerDetails,
        string? reconciliationError,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(providerDetails);

        var episodeLock = EpisodeLocks.GetOrAdd(subscription.Id, _ => new SemaphoreSlim(1, 1));
        await episodeLock.WaitAsync(cancellationToken);
        try
        {
            var anomaly = await UpsertEpisodeAsync(
                subscription,
                providerDetails,
                reconciliationError,
                cancellationToken);
            await SendPendingNotificationsAsync(anomaly.Id, user, baseUrl, cancellationToken);
            return await GetByIdAsync(anomaly.Id, cancellationToken) ?? anomaly;
        }
        finally
        {
            episodeLock.Release();
        }
    }

    public async Task ResolveOpenEpisodeAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await context.PayPalSubscriptionAnomalies
            .Where(anomaly => anomaly.SubscriptionId == subscriptionId && anomaly.ResolvedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(anomaly => anomaly.ResolvedAtUtc, now)
                    .SetProperty(anomaly => anomaly.NotificationClaimId, (string?)null)
                    .SetProperty(anomaly => anomaly.NotificationClaimedAtUtc, (DateTime?)null),
                cancellationToken);
    }

    private async Task<PayPalSubscriptionAnomaly> UpsertEpisodeAsync(
        Subscription subscription,
        PayPalSubscriptionDetails details,
        string? reconciliationError,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var anomaly = await context.PayPalSubscriptionAnomalies.SingleOrDefaultAsync(
            existing => existing.SubscriptionId == subscription.Id && existing.ResolvedAtUtc == null,
            cancellationToken);
        var isNewEpisode = anomaly == null;

        if (anomaly == null)
        {
            anomaly = new PayPalSubscriptionAnomaly
            {
                SubscriptionId = subscription.Id,
                UserId = subscription.UserId,
                PayPalSubscriptionId = subscription.PayPalSubscriptionId ?? details.Id,
                CorrelationId = Guid.NewGuid().ToString("D"),
                DetectedAtUtc = now
            };
            context.PayPalSubscriptionAnomalies.Add(anomaly);
        }

        anomaly.ProviderStatus = details.Status ?? string.Empty;
        anomaly.LocalStatus = subscription.Status ?? string.Empty;
        anomaly.ProviderStartDate = details.StartDate;
        anomaly.ProviderTrialEndDate = details.TrialEndDate;
        anomaly.ProviderLastPaymentDate = details.LastPaymentDate;
        anomaly.ProviderNextBillingDate = details.NextBillingDate;
        anomaly.LocalTrialEndDate = subscription.TrialEndDate;
        anomaly.LocalLastPaymentDate = subscription.LastPaymentDate;
        anomaly.LocalNextBillingDate = subscription.NextBillingDate;
        anomaly.LocalEndDate = subscription.EndDate;
        anomaly.FailedPaymentsCount = details.FailedPaymentsCount;
        anomaly.ReconciliationError = Truncate(reconciliationError, 1000);
        anomaly.LastObservedAtUtc = now;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return anomaly;
        }
        catch (DbUpdateException) when (isNewEpisode)
        {
            context.ChangeTracker.Clear();
            var concurrentEpisode = await context.PayPalSubscriptionAnomalies.SingleOrDefaultAsync(
                existing => existing.SubscriptionId == subscription.Id && existing.ResolvedAtUtc == null,
                cancellationToken);
            if (concurrentEpisode != null)
            {
                return concurrentEpisode;
            }

            throw;
        }
    }

    private async Task SendPendingNotificationsAsync(
        int anomalyId,
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var claimId = Guid.NewGuid().ToString("D");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var staleClaimBoundary = now.AddMinutes(-PayPalSubscriptionDefaults.AnomalyNotificationClaimMinutes);

        await using (var claimContext = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var claimed = await claimContext.PayPalSubscriptionAnomalies
                .Where(anomaly => anomaly.Id == anomalyId
                    && anomaly.ResolvedAtUtc == null
                    && (anomaly.UserEmailSentAtUtc == null || anomaly.AdminEmailSentAtUtc == null)
                    && (anomaly.NotificationClaimedAtUtc == null
                        || anomaly.NotificationClaimedAtUtc < staleClaimBoundary))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(anomaly => anomaly.NotificationClaimId, claimId)
                        .SetProperty(anomaly => anomaly.NotificationClaimedAtUtc, now),
                    cancellationToken);
            if (claimed == 0)
            {
                return;
            }
        }

        var anomaly = await GetByIdAsync(anomalyId, cancellationToken);
        if (anomaly == null || !string.Equals(anomaly.NotificationClaimId, claimId, StringComparison.Ordinal))
        {
            return;
        }

        var userSent = anomaly.UserEmailSentAtUtc.HasValue;
        var adminSent = anomaly.AdminEmailSentAtUtc.HasValue;
        try
        {
            if (!userSent && !string.IsNullOrWhiteSpace(user.Email))
            {
                userSent = await _emailService.SendEmailAsync(
                    user.Email,
                    "StreamTunes - PayPal subscription needs attention",
                    BuildUserEmail(user, anomaly, baseUrl));
            }

            var customerServiceEmail = _configuration[AppSettingKeys.EmailCustomerServiceEmail];
            if (!adminSent && !string.IsNullOrWhiteSpace(customerServiceEmail))
            {
                adminSent = await _emailService.SendEmailAsync(
                    customerServiceEmail,
                    $"StreamTunes - PayPal subscription mismatch {anomaly.CorrelationId}",
                    BuildAdminEmail(user, anomaly));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send PayPal subscription anomaly notifications for correlation {CorrelationId}",
                anomaly.CorrelationId);
        }
        finally
        {
            await CompleteClaimAsync(anomalyId, claimId, userSent, adminSent, cancellationToken);
        }
    }

    private async Task CompleteClaimAsync(
        int anomalyId,
        string claimId,
        bool userSent,
        bool adminSent,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var anomaly = await context.PayPalSubscriptionAnomalies.SingleOrDefaultAsync(
            existing => existing.Id == anomalyId && existing.NotificationClaimId == claimId,
            cancellationToken);
        if (anomaly == null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        anomaly.UserEmailSentAtUtc ??= userSent ? now : null;
        anomaly.AdminEmailSentAtUtc ??= adminSent ? now : null;
        anomaly.NotificationClaimId = null;
        anomaly.NotificationClaimedAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PayPalSubscriptionAnomaly?> GetByIdAsync(
        int anomalyId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PayPalSubscriptionAnomalies
            .AsNoTracking()
            .SingleOrDefaultAsync(anomaly => anomaly.Id == anomalyId, cancellationToken);
    }

    private string BuildUserEmail(
        ApplicationUser user,
        PayPalSubscriptionAnomaly anomaly,
        string baseUrl)
    {
        var displayName = WebUtility.HtmlEncode(user.UserName ?? user.Email ?? "valued customer");
        var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";
        var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
        var payPalUrl = _configuration[AppSettingKeys.PayPalAccountManagementUrl];
        var statusGuidance = string.Equals(
            anomaly.ProviderStatus,
            SubscriptionStatuses.Suspended,
            StringComparison.OrdinalIgnoreCase)
            ? "PayPal reports that the agreement is suspended. Please review the payment issue in PayPal or stop the agreement."
            : "PayPal reports that the agreement is active, but StreamTunes cannot confirm a current paid period or free trial.";

        var body = new StringBuilder();
        body.Append($"<div style='text-align:center;margin-bottom:20px;'><img src='{WebUtility.HtmlEncode(logoUrl)}' alt='StreamTunes Logo' style='max-width:150px;height:auto;' /></div>");
        body.Append($"<p>Hello {displayName},</p><p>{WebUtility.HtmlEncode(statusGuidance)}</p>");
        body.Append("<p>Starting another StreamTunes subscription is temporarily blocked to prevent overlapping recurring charges. Creator tips and other one-time payments are not affected.</p>");
        body.Append($"<p><a href='{WebUtility.HtmlEncode(manageAccountUrl)}'>Refresh your subscription in Manage Account</a></p>");
        if (!string.IsNullOrWhiteSpace(payPalUrl))
        {
            body.Append($"<p><a href='{WebUtility.HtmlEncode(payPalUrl)}'>Open PayPal automatic payments</a></p>");
        }
        body.Append($"<p>If the issue continues, contact customer service and include correlation ID <strong>{WebUtility.HtmlEncode(anomaly.CorrelationId)}</strong>.</p>");
        return body.ToString();
    }

    private static string BuildAdminEmail(ApplicationUser user, PayPalSubscriptionAnomaly anomaly)
    {
        static string Value(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "Not available");

        return $"""
            <p>A PayPal subscription mismatch requires attention.</p>
            <ul>
              <li>Correlation ID: {Value(anomaly.CorrelationId)}</li>
              <li>User ID / email: {anomaly.UserId} / {Value(user.Email)}</li>
              <li>Local subscription ID: {anomaly.SubscriptionId}</li>
              <li>PayPal subscription ID: {Value(anomaly.PayPalSubscriptionId)}</li>
              <li>Local / provider status: {Value(anomaly.LocalStatus)} / {Value(anomaly.ProviderStatus)}</li>
              <li>Provider start: {Value(anomaly.ProviderStartDate)}</li>
              <li>Provider trial end: {Value(anomaly.ProviderTrialEndDate)}</li>
              <li>Provider last payment: {Value(anomaly.ProviderLastPaymentDate)}</li>
              <li>Provider next billing: {Value(anomaly.ProviderNextBillingDate)}</li>
              <li>Local trial end: {Value(anomaly.LocalTrialEndDate)}</li>
              <li>Local last payment: {Value(anomaly.LocalLastPaymentDate)}</li>
              <li>Local next billing: {Value(anomaly.LocalNextBillingDate)}</li>
              <li>Local entitlement end: {Value(anomaly.LocalEndDate)}</li>
              <li>Failed-payment count: {anomaly.FailedPaymentsCount}</li>
              <li>Reconciliation error: {Value(anomaly.ReconciliationError)}</li>
            </ul>
            """;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}
