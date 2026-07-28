using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using System.Globalization;

#nullable enable

namespace MusicSalesApp.Services;

public class PayPalCheckoutHygieneService : IPayPalCheckoutHygieneService
{
    private static readonly TimeSpan StalePendingCheckoutAge =
        TimeSpan.FromHours(PayPalSubscriptionDefaults.StalePendingCheckoutHours);

    private static readonly TimeSpan NotificationGrace =
        TimeSpan.FromMinutes(PayPalSubscriptionDefaults.AnomalyNotificationGraceMinutes);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IPayPalSubscriptionManagementService _payPalSubscriptionManagementService;
    private readonly IPayPalSubscriptionAnomalyService _anomalyService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PayPalCheckoutHygieneService> _logger;

    public PayPalCheckoutHygieneService(
        IDbContextFactory<AppDbContext> contextFactory,
        IPayPalSubscriptionManagementService payPalSubscriptionManagementService,
        IPayPalSubscriptionAnomalyService anomalyService,
        ISubscriptionService subscriptionService,
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        TimeProvider timeProvider,
        ILogger<PayPalCheckoutHygieneService> logger)
    {
        _contextFactory = contextFactory;
        _payPalSubscriptionManagementService = payPalSubscriptionManagementService;
        _anomalyService = anomalyService;
        _subscriptionService = subscriptionService;
        _userManager = userManager;
        _emailService = emailService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task<int> CleanupStalePendingCheckoutsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.Subtract(StalePendingCheckoutAge);

            // APPROVAL_PENDING only, deliberately. MapPayPalStatus normalizes the provider's
            // APPROVED to APPROVAL_PENDING before anything is persisted, so no local row can hold
            // APPROVED - and GetPendingSubscriptionAsync, which every downstream call relies on,
            // matches APPROVAL_PENDING alone.
            var stale = await context.Subscriptions
                .AsNoTracking()
                .Where(subscription => subscription.BillingSource == BillingSources.PayPal
                    && subscription.Status == SubscriptionStatuses.ApprovalPending
                    && subscription.PayPalSubscriptionId != null
                    && subscription.CreatedAt <= cutoff)
                .OrderByDescending(subscription => subscription.CreatedAt)
                .ToListAsync();

            // Only the newest pending row per user is actionable - every downstream call resolves
            // the user's pending checkout by user id, not by row id. Selecting older rows too would
            // fill the batch with entries that can never be processed.
            var candidates = stale
                .GroupBy(subscription => subscription.UserId)
                .Select(group => group.First())
                .OrderBy(subscription => subscription.CreatedAt)
                .Take(PayPalSubscriptionDefaults.StalePendingCheckoutBatchSize)
                .ToList();

            if (stale.Count > candidates.Count)
            {
                _logger.LogInformation(
                    "Skipping {Count} superseded stale PayPal checkouts; only the newest row per user is actionable",
                    stale.Count - candidates.Count);
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("No stale PayPal checkouts found for cleanup");
                return 0;
            }

            _logger.LogInformation(
                "Found {Count} PayPal checkouts still APPROVAL_PENDING on or before {Cutoff}",
                candidates.Count,
                cutoff);

            var closedCount = 0;
            foreach (var candidate in candidates)
            {
                if (await TryCloseStaleCheckoutAsync(candidate))
                {
                    closedCount++;
                }
            }

            _logger.LogInformation("Closed {Count} stale PayPal checkouts", closedCount);
            return closedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up stale PayPal checkouts");
            throw;
        }
    }

    private async Task<bool> TryCloseStaleCheckoutAsync(Subscription candidate)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(
                candidate.UserId.ToString(CultureInfo.InvariantCulture));
            if (user == null)
            {
                _logger.LogWarning(
                    "Skipping stale PayPal checkout {SubscriptionId} because user {UserId} was not found",
                    candidate.Id,
                    candidate.UserId);
                return false;
            }

            // The id is passed down so the abandon re-checks it against the same read it acts on.
            // Checking here alone would leave a window in which a checkout started in between is
            // torn down instead of the stale row.
            if (!await _payPalSubscriptionManagementService.AbandonPendingCheckoutAsync(
                    user,
                    candidate.Id,
                    CancellationToken.None))
            {
                // Never delete locally when PayPal could not confirm the agreement's fate.
                _logger.LogWarning(
                    "Could not close stale PayPal checkout {SubscriptionId} for user {UserId}; leaving it untouched",
                    candidate.Id,
                    candidate.UserId);
                return false;
            }

            // The delete path removes the episode via the cascade FK, but the cancel path leaves the
            // row alive as CANCELLED with its episode still open.
            await _anomalyService.ResolveOpenEpisodeAsync(candidate.Id, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error closing stale PayPal checkout {SubscriptionId} for user {UserId}",
                candidate.Id,
                candidate.UserId);
            return false;
        }
    }

    // Runs hourly; a batch that overruns the hour must not stack with the next tick.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task<int> ReobserveOpenMismatchEpisodesAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var boundary = _timeProvider.GetUtcNow().UtcDateTime.Subtract(NotificationGrace);

            var pendingNotification = await context.PayPalSubscriptionAnomalies
                .AsNoTracking()
                .Where(anomaly => anomaly.ResolvedAtUtc == null
                    && (anomaly.UserEmailSentAtUtc == null || anomaly.AdminEmailSentAtUtc == null)
                    && anomaly.DetectedAtUtc <= boundary)
                .OrderBy(anomaly => anomaly.DetectedAtUtc)
                // Each episode costs at least one PayPal round trip. Cap the batch so a provider
                // incident that opens hundreds of episodes cannot produce an hours-long run.
                .Take(PayPalSubscriptionDefaults.StalePendingCheckoutBatchSize)
                .Select(anomaly => new { anomaly.Id, anomaly.PayPalSubscriptionId })
                .ToListAsync();

            if (pendingNotification.Count == 0)
            {
                return 0;
            }

            _logger.LogInformation(
                "Re-observing {Count} open PayPal mismatch episodes past the notification grace window",
                pendingNotification.Count);

            var baseUrl = _emailService.GetAppBaseUrl();
            var reobservedCount = 0;
            foreach (var episode in pendingNotification)
            {
                if (string.IsNullOrWhiteSpace(episode.PayPalSubscriptionId))
                {
                    continue;
                }

                try
                {
                    // A single reconcile either resolves the episode (it self-healed) or re-records
                    // it with LastObservedAtUtc past the grace, which releases the withheld emails.
                    await _payPalSubscriptionManagementService.ReconcileSubscriptionAsync(
                        episode.PayPalSubscriptionId,
                        baseUrl,
                        CancellationToken.None);
                    reobservedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not re-observe PayPal mismatch episode {AnomalyId}",
                        episode.Id);
                }
            }

            return reobservedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-observing open PayPal mismatch episodes");
            throw;
        }
    }
}
