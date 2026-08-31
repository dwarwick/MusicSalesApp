#nullable enable

using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class PayPalEntitlementDriftService : IPayPalEntitlementDriftService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IPayPalSubscriptionManagementService _payPalSubscriptionManagementService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IEmailService _emailService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PayPalEntitlementDriftService> _logger;

    public PayPalEntitlementDriftService(
        IDbContextFactory<AppDbContext> contextFactory,
        IPayPalSubscriptionManagementService payPalSubscriptionManagementService,
        ISubscriptionService subscriptionService,
        IEmailService emailService,
        TimeProvider timeProvider,
        ILogger<PayPalEntitlementDriftService> logger)
    {
        _contextFactory = contextFactory;
        _payPalSubscriptionManagementService = payPalSubscriptionManagementService;
        _subscriptionService = subscriptionService;
        _emailService = emailService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ReconcileDriftedSubscriptionsAsync()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await LoadCandidatesAsync(now);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("No PayPal subscriptions needed an entitlement drift check");
            return 0;
        }

        _logger.LogInformation(
            "Verifying {Count} PayPal subscriptions against the provider for entitlement drift",
            candidates.Count);

        var baseUrl = _emailService.GetAppBaseUrl();
        var attempts = new List<(Subscription Candidate, PayPalReconcileAttempt Attempt)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            attempts.Add((candidate, await TryReconcileAsync(candidate, baseUrl)));
        }

        var outcomes = attempts.Select(attempt => attempt.Attempt.Outcome).ToList();
        var settleMissing = CanTrustA404(outcomes);

        if (outcomes.All(outcome => outcome == PayPalReconcileOutcome.Unverifiable))
        {
            // Not a single answer from PayPal. Expired or revoked credentials look exactly like
            // this, and at Warning per row it would be 200 lines a night that nobody reads.
            _logger.LogError(
                "None of the {Count} PayPal subscriptions checked for entitlement drift could be verified with the provider",
                attempts.Count);
        }

        var settledCount = 0;
        foreach (var (candidate, attempt) in attempts)
        {
            if (await ApplyAsync(candidate, attempt, settleMissing))
            {
                settledCount++;
            }
        }

        // Stamped after the settlements, not before. Bookkeeping must never be able to block
        // remediation: a failure here would otherwise skip every settlement in the batch after the
        // provider calls had already been spent.
        try
        {
            await StampCheckedAsync(candidates, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record when PayPal subscriptions were last verified");
        }

        _logger.LogInformation("Settled {Count} drifted PayPal subscriptions", settledCount);
        return settledCount;
    }

    /// <summary>
    /// Whether a 404 in this batch may be acted on.
    ///
    /// <para>
    /// Requires positive proof that the connection to PayPal works: at least one agreement the
    /// provider actually recognised. Without that, a 404 is far more likely to mean credentials
    /// pointed at the wrong environment - in which case every agreement 404s and settling would
    /// cancel every subscriber in turn - than to mean this particular agreement is dead.
    /// </para>
    ///
    /// <para>
    /// Stated this way rather than as "were they all missing?" on purpose. That phrasing had two
    /// holes: a batch of one was never guarded at all, and a single unrelated network blip among
    /// 200 rows made the batch "not all missing" and let 199 false 404s through.
    /// </para>
    /// </summary>
    private bool CanTrustA404(IReadOnlyCollection<PayPalReconcileOutcome> outcomes)
    {
        if (outcomes.Any(outcome => outcome == PayPalReconcileOutcome.Reconciled))
        {
            return true;
        }

        if (outcomes.Any(outcome => outcome == PayPalReconcileOutcome.ProviderConfirmedMissing))
        {
            _logger.LogError(
                "PayPal reported subscriptions as missing but recognised none of the {Count} checked this run; treating that as a broken provider connection and settling none",
                outcomes.Count);
        }

        return false;
    }

    private async Task<List<Subscription>> LoadCandidatesAsync(DateTime now)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Mirrors the entitlement test in SubscriptionService.GetActiveSubscriptionAsync, widened
        // to include SUSPENDED and rows stuck at ACTIVE with nothing to expire - the missed
        // CANCELLED webhook leaves exactly such a row, and NormalizeExpiredSubscriptionsAsync can
        // never clear it because there is no EndDate to fall due.
        //
        // Ordered by when each row was last verified, nulls first, so the batch cap rotates through
        // the whole population instead of re-checking the same lowest ids every night.
        var candidates = await context.Subscriptions
            .AsNoTracking()
            .Where(subscription => subscription.BillingSource == BillingSources.PayPal
                && subscription.PayPalSubscriptionId != null
                && (subscription.Status == SubscriptionStatuses.Active
                    || subscription.Status == SubscriptionStatuses.Suspended
                    || (subscription.Status == SubscriptionStatuses.Cancelled
                        && subscription.EndDate != null
                        && subscription.EndDate > now)))
            .OrderBy(subscription => subscription.LastProviderCheckAtUtc)
            .ThenBy(subscription => subscription.Id)
            // One over the cap, purely to detect truncation.
            .Take(PayPalSubscriptionDefaults.EntitlementDriftBatchSize + 1)
            .ToListAsync();

        if (candidates.Count > PayPalSubscriptionDefaults.EntitlementDriftBatchSize)
        {
            candidates.RemoveAt(candidates.Count - 1);
            _logger.LogInformation(
                "More than {BatchSize} PayPal subscriptions are due an entitlement drift check; the rest are covered by subsequent runs, oldest check first",
                PayPalSubscriptionDefaults.EntitlementDriftBatchSize);
        }

        return candidates;
    }

    private async Task<PayPalReconcileAttempt> TryReconcileAsync(Subscription candidate, string baseUrl)
    {
        try
        {
            // Deliberately the same reconcile the webhook handler performs. This sweep is a
            // catch-up for a missed notification, not a second opinion: inventing its own
            // revocation policy would let a background job and a webhook disagree about the same
            // agreement.
            //
            // Silent, though. By definition this only fires for a notification that never arrived,
            // so the transition is always being recorded late - sometimes months late. A
            // cancellation email at that point announces something the subscriber did long ago as
            // if it just happened. The live webhook path still emails at the right moment.
            return await _payPalSubscriptionManagementService.TryReconcileSubscriptionAsync(
                candidate.PayPalSubscriptionId!,
                baseUrl,
                sendTransitionEmails: false,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error verifying PayPal subscription {SubscriptionId} ({PayPalSubscriptionId}) for user {UserId}",
                candidate.Id,
                candidate.PayPalSubscriptionId,
                candidate.UserId);
            return PayPalReconcileAttempt.Unverifiable;
        }
    }

    private async Task<bool> ApplyAsync(
        Subscription candidate,
        PayPalReconcileAttempt attempt,
        bool settleMissing)
    {
        switch (attempt.Outcome)
        {
            case PayPalReconcileOutcome.Unverifiable:
                // A transient provider outage. Warning, not Error: this must not page an admin,
                // and the row is simply checked again on the next run.
                _logger.LogWarning(
                    "Could not verify PayPal subscription {SubscriptionId} ({PayPalSubscriptionId}) for user {UserId} with the provider; leaving it untouched",
                    candidate.Id,
                    candidate.PayPalSubscriptionId,
                    candidate.UserId);
                return false;

            case PayPalReconcileOutcome.ProviderConfirmedMissing:
                if (!settleMissing)
                {
                    return false;
                }

                // PayPal has no such agreement, so it cannot bill anyone and the local row's claim
                // to entitlement is refuted. Cancel rather than delete: deleting would erase the
                // billing record, and HasPriorActivatedSubscriptionAsync reads LastPaymentDate and
                // the trial dates to decide free-trial eligibility.
                //
                // Note what cancelling does not preserve: that predicate also counts a status of
                // ACTIVE or SUSPENDED as proof of activation, and this write removes it. A row
                // whose *only* activation evidence was its status therefore becomes trial-eligible
                // again. Adding CancelledAt to that predicate would fix it and break something
                // worse - an abandoned checkout is also cancelled, and would then be denied a first
                // trial it never had.
                //
                // The candidate's own end date is passed through, not null: a row already CANCELLED
                // with a future EndDate is someone who cancelled but paid through, and nulling it
                // would silently confiscate the days they bought.
                var cancelled = await _subscriptionService.CancelPayPalSubscriptionAsync(
                    candidate.PayPalSubscriptionId!,
                    candidate.EndDate);
                _logger.LogWarning(
                    "Settled PayPal subscription {SubscriptionId} ({PayPalSubscriptionId}) for user {UserId}: it was {PreviousStatus} locally but the provider no longer knows the agreement",
                    candidate.Id,
                    candidate.PayPalSubscriptionId,
                    candidate.UserId,
                    candidate.Status);
                return cancelled;

            default:
                var current = attempt.Result!.Subscription.Status;
                if (string.Equals(current, candidate.Status, StringComparison.Ordinal))
                {
                    return false;
                }

                _logger.LogWarning(
                    "Settled drifted PayPal subscription {SubscriptionId} ({PayPalSubscriptionId}) for user {UserId}: local status was {PreviousStatus}, provider reports {CurrentStatus}",
                    candidate.Id,
                    candidate.PayPalSubscriptionId,
                    candidate.UserId,
                    candidate.Status,
                    current);
                return true;
        }
    }

    private async Task StampCheckedAsync(IReadOnlyCollection<Subscription> candidates, DateTime checkedAtUtc)
    {
        var ids = candidates.Select(candidate => candidate.Id).ToList();

        // A fresh context, loaded after the reconciles have run, so the entities carry whatever
        // status reconciliation just wrote. Only this column is modified, so the UPDATE cannot
        // clobber it either way.
        await using var context = await _contextFactory.CreateDbContextAsync();
        var rows = await context.Subscriptions
            .Where(subscription => ids.Contains(subscription.Id))
            .ToListAsync();

        foreach (var row in rows)
        {
            row.LastProviderCheckAtUtc = checkedAtUtc;
        }

        await context.SaveChangesAsync();
    }
}
