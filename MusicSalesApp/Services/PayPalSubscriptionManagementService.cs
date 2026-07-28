using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System.Collections.Concurrent;
using System.Globalization;

#nullable enable

namespace MusicSalesApp.Services;

public sealed class PayPalSubscriptionManagementService : IPayPalSubscriptionManagementService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> CheckoutLocks = new();

    private readonly IPayPalSubscriptionApiService _payPalApi;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ISubscriptionConfirmationEmailService _confirmationEmailService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IPayPalSubscriptionAnomalyService _anomalyService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PayPalSubscriptionManagementService> _logger;

    public PayPalSubscriptionManagementService(
        IPayPalSubscriptionApiService payPalApi,
        ISubscriptionService subscriptionService,
        IAppSettingsService appSettingsService,
        ISubscriptionConfirmationEmailService confirmationEmailService,
        IAccountEmailService accountEmailService,
        IPayPalSubscriptionAnomalyService anomalyService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<PayPalSubscriptionManagementService> logger)
    {
        _payPalApi = payPalApi;
        _subscriptionService = subscriptionService;
        _appSettingsService = appSettingsService;
        _confirmationEmailService = confirmationEmailService;
        _accountEmailService = accountEmailService;
        _anomalyService = anomalyService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PayPalWebOfferQuote> GetOfferQuoteAsync(
        int? userId,
        CancellationToken cancellationToken = default)
    {
        var configuredOffer = await _appSettingsService.GetPayPalWebSubscriptionOfferAsync();

        if (configuredOffer == null)
        {
            throw new InvalidOperationException(
                "A PayPal web subscription offer has not been configured.");
        }

        var isFirstTimeSubscriber = !userId.HasValue
            || !await _subscriptionService.HasPriorActivatedSubscriptionAsync(userId.Value);

        var selectedPlan = configuredOffer.PrimaryPlan;
        if (!isFirstTimeSubscriber && configuredOffer.PrimaryPlan.HasFreeTrial)
        {
            selectedPlan = configuredOffer.ResubscriberPlan
                ?? throw new InvalidOperationException(
                    "The configured PayPal trial offer does not have a no-trial re-subscriber plan.");
        }

        return new PayPalWebOfferQuote
        {
            PlanId = selectedPlan.Id,
            PlanName = selectedPlan.Name,
            RegularPrice = selectedPlan.RegularPrice,
            CurrencyCode = selectedPlan.CurrencyCode,
            IntervalUnit = selectedPlan.IntervalUnit,
            IntervalCount = selectedPlan.IntervalCount,
            TrialDays = isFirstTimeSubscriber ? selectedPlan.TrialDays : null,
            SettingsVersion = configuredOffer.Version,
            IsFirstTimeSubscriber = isFirstTimeSubscriber
        };
    }

    public async Task<PayPalCheckoutResult> CreateSubscriptionAsync(
        ApplicationUser user,
        bool agreeToTerms,
        int? displayedOfferVersion,
        string? displayedPlanId,
        string fallbackBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!agreeToTerms)
        {
            return PayPalCheckoutResult.Failed("You must agree to the subscription terms and conditions.");
        }

        if (user.IsSubscriptionBlocked)
        {
            return PayPalCheckoutResult.Failed(
                "Your account is not eligible for a new subscription. Please contact support for assistance.");
        }

        var checkoutLock = CheckoutLocks.GetOrAdd(user.Id, static _ => new SemaphoreSlim(1, 1));
        if (!await checkoutLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return PayPalCheckoutResult.Failed(
                "A PayPal checkout is already being started for this account. Please use the checkout that just opened.");
        }

        try
        {
            return await CreateSubscriptionUnderLockAsync(
                user,
                displayedOfferVersion,
                displayedPlanId,
                fallbackBaseUrl,
                cancellationToken);
        }
        finally
        {
            checkoutLock.Release();
        }
    }

    private async Task<PayPalCheckoutResult> CreateSubscriptionUnderLockAsync(
        ApplicationUser user,
        int? displayedOfferVersion,
        string? displayedPlanId,
        string fallbackBaseUrl,
        CancellationToken cancellationToken)
    {

        if (await _subscriptionService.GetActiveSubscriptionAsync(user.Id) != null)
        {
            return PayPalCheckoutResult.Failed(
                "An existing subscription still provides access. Wait until it ends before starting another subscription.");
        }

        var latestSubscription = await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        if (latestSubscription != null
            && string.Equals(latestSubscription.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(latestSubscription.PayPalSubscriptionId)
            && !IsProviderCancelledStatus(latestSubscription.Status)
            && !string.Equals(
                latestSubscription.Status,
                SubscriptionStatuses.ApprovalPending,
                StringComparison.OrdinalIgnoreCase))
        {
            var reconciled = await ReconcileSubscriptionAsync(
                latestSubscription.PayPalSubscriptionId,
                fallbackBaseUrl,
                cancellationToken);
            if (reconciled == null)
            {
                return PayPalCheckoutResult.Failed(
                    "The previous PayPal subscription could not be verified. A second subscription was not started; please try again later.");
            }

            latestSubscription = reconciled.Subscription;
        }

        if (latestSubscription != null
            && (string.Equals(latestSubscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal)
                || string.Equals(latestSubscription.Status, SubscriptionStatuses.Suspended, StringComparison.Ordinal)))
        {
            return PayPalCheckoutResult.Failed(
                "A previous subscription is still active or suspended with its billing provider. Resolve it before starting another subscription.");
        }

        var pendingCheckoutError = await ClearAbandonedPendingCheckoutAsync(
            user,
            fallbackBaseUrl,
            cancellationToken);
        if (pendingCheckoutError != null)
        {
            return PayPalCheckoutResult.Failed(pendingCheckoutError);
        }

        var quote = await GetOfferQuoteAsync(user.Id, cancellationToken);
        if (displayedOfferVersion != quote.SettingsVersion
            || !string.Equals(displayedPlanId, quote.PlanId, StringComparison.Ordinal))
        {
            return PayPalCheckoutResult.Failed(
                "The subscription offer or your trial eligibility changed. Refresh the page and review the current terms before subscribing.");
        }

        PayPalPlan livePlan;
        try
        {
            livePlan = await _payPalApi.GetPlanAsync(quote.PlanId!, cancellationToken);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogError(ex, "Unable to validate the PayPal plan for user {UserId}", user.Id);
            return PayPalCheckoutResult.Failed(
                "The subscription offer could not be verified with PayPal. Please try again later.");
        }

        if (livePlan == null || !PlanMatchesQuote(livePlan, quote))
        {
            _logger.LogError(
                "PayPal plan {PlanId} no longer matches advertised offer version {OfferVersion}",
                livePlan?.Id ?? quote.PlanId,
                quote.SettingsVersion);
            return PayPalCheckoutResult.Failed(
                "The PayPal plan terms changed. An administrator must refresh the offered plan before checkout can continue.");
        }

        var baseUrl = ResolveBaseUrl(fallbackBaseUrl);
        var returnUrl = $"{baseUrl}/manage-account?success=true";
        var cancelUrl = $"{baseUrl}/manage-account?success=false";

        try
        {
            var created = await _payPalApi.CreateSubscriptionAsync(
                livePlan.Id,
                returnUrl,
                cancelUrl,
                cancellationToken);

            try
            {
                await _subscriptionService.CreateSubscriptionAsync(
                    user.Id,
                    created.Id,
                    livePlan.Id,
                    livePlan.RegularPrice,
                    livePlan.CurrencyCode,
                    quote.SettingsVersion,
                    DateTime.UtcNow);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Subscription entitlement changed during PayPal checkout for user {UserId}", user.Id);
                try
                {
                    await _payPalApi.CancelSubscriptionAsync(
                        created.Id,
                        PayPalSubscriptionDefaults.UserCancellationReason,
                        cancellationToken);
                }
                catch (PayPalSubscriptionApiException cancellationException)
                {
                    _logger.LogError(
                        cancellationException,
                        "Unable to clean up unapproved PayPal subscription {SubscriptionId} after an entitlement race",
                        created.Id);
                }

                return PayPalCheckoutResult.Failed(
                    "An existing subscription now provides access. No new PayPal subscription was started.");
            }

            return new PayPalCheckoutResult(true, created.ApprovalUrl, SubscriptionId: created.Id);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogError(ex, "PayPal checkout creation failed for user {UserId}", user.Id);
            return PayPalCheckoutResult.Failed("PayPal could not start the subscription. Please try again later.");
        }
    }

    public async Task<PayPalActivationResult> ActivateCurrentSubscriptionAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var pendingSubscription = await _subscriptionService.GetPendingSubscriptionAsync(user.Id);
        var activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);

        if (pendingSubscription != null
            && activeSubscription != null
            && pendingSubscription.Id != activeSubscription.Id)
        {
            var stopped = await StopOverlappingPayPalSubscriptionAsync(
                pendingSubscription,
                baseUrl,
                cancellationToken);
            return new PayPalActivationResult(
                false,
                stopped
                    ? "Another subscription already provides access. The overlapping PayPal agreement was stopped."
                    : "Another subscription already provides access, and PayPal could not safely close the overlapping agreement. Please contact support.");
        }

        var subscription = pendingSubscription ?? activeSubscription;

        if (subscription == null
            || !string.Equals(subscription.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
        {
            return new PayPalActivationResult(false, "No PayPal subscription was found for this account.");
        }

        var result = await ReconcileSubscriptionAsync(
            subscription.PayPalSubscriptionId,
            baseUrl,
            cancellationToken);
        if (result == null)
        {
            return new PayPalActivationResult(false, "The PayPal subscription could not be reconciled.");
        }

        if (!string.Equals(result.Subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal))
        {
            return new PayPalActivationResult(
                false,
                $"The PayPal subscription is not active. Current status: {result.Subscription.Status}.");
        }

        var isTrial = result.Subscription.TrialEndDate > DateTime.UtcNow
            && !result.Subscription.LastPaymentDate.HasValue;
        if (!isTrial && !result.Subscription.LastPaymentDate.HasValue)
        {
            return new PayPalActivationResult(
                false,
                "PayPal has not confirmed either an active trial or a completed payment.");
        }

        return new PayPalActivationResult(true, IsTrial: isTrial);
    }

    public async Task<PayPalSubscriptionReconciliationResult?> ReconcileSubscriptionAsync(
        string paypalSubscriptionId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        PayPalSubscriptionDetails details;
        try
        {
            details = await _payPalApi.GetSubscriptionAsync(paypalSubscriptionId, cancellationToken);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogWarning(ex, "Unable to retrieve PayPal subscription {SubscriptionId}", paypalSubscriptionId);
            return null;
        }

        if (details == null)
        {
            _logger.LogWarning("PayPal subscription {SubscriptionId} was not found", paypalSubscriptionId);

            // A provider-confirmed 404 means the agreement does not exist, so it cannot bill anyone
            // and there is no mismatch left to escalate. Resolve any open episode here: this path
            // never reaches SynchronizeAnomalyEpisodeAsync, so without it the episode would be
            // re-observed forever by the hygiene job and never notified or closed.
            var missingSubscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
            if (missingSubscription != null)
            {
                await _anomalyService.ResolveOpenEpisodeAsync(missingSubscription.Id, cancellationToken);
            }

            return null;
        }

        var localSubscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        if (localSubscription != null && IsPotentiallyBillableStatus(details.Status))
        {
            var otherProviderSubscription = await _subscriptionService
                .GetCurrentSubscriptionFromOtherProviderAsync(
                    localSubscription.UserId,
                    BillingSources.PayPal);
            if (otherProviderSubscription != null)
            {
                var previousStatus = localSubscription.Status;
                if (!await StopOverlappingPayPalSubscriptionAsync(
                        localSubscription,
                        baseUrl,
                        cancellationToken))
                {
                    _logger.LogError(
                        "PayPal subscription {SubscriptionId} overlaps current {BillingSource} subscription {OtherSubscriptionId} and could not be stopped",
                        paypalSubscriptionId,
                        otherProviderSubscription.BillingSource,
                        otherProviderSubscription.Id);
                    return null!;
                }

                var stoppedSubscription = await _subscriptionService
                    .GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
                return stoppedSubscription == null
                    ? null!
                    : new PayPalSubscriptionReconciliationResult
                    {
                        Subscription = stoppedSubscription,
                        PreviousStatus = previousStatus
                    };
            }
        }

        var reconciliation = await ReconcileRetrievedSubscriptionAsync(details, baseUrl);
        await SynchronizeAnomalyEpisodeAsync(
            localSubscription,
            details,
            reconciliation,
            reconciliation == null ? "The local subscription could not be reconciled with PayPal." : null,
            baseUrl,
            cancellationToken);
        return reconciliation;
    }

    private async Task<PayPalSubscriptionReconciliationResult> ReconcileRetrievedSubscriptionAsync(
        PayPalSubscriptionDetails details,
        string baseUrl,
        bool sendTransitionEmails = true)
    {
        var paypalSubscriptionId = details.Id;
        if (string.IsNullOrWhiteSpace(paypalSubscriptionId))
        {
            _logger.LogWarning("PayPal returned subscription details without an ID");
            return null!;
        }

        var localSubscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        if (localSubscription == null)
        {
            _logger.LogWarning("PayPal subscription {SubscriptionId} has no local record", paypalSubscriptionId);
            return null!;
        }

        if (localSubscription.UserId <= 0)
        {
            return null!;
        }

        if (!string.IsNullOrWhiteSpace(localSubscription.PayPalPlanId)
            && !string.Equals(localSubscription.PayPalPlanId, details.PlanId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "PayPal subscription {SubscriptionId} returned plan {ProviderPlanId}, expected {LocalPlanId}",
                paypalSubscriptionId,
                details.PlanId,
                localSubscription.PayPalPlanId);
            return null!;
        }

        var result = await _subscriptionService.ReconcilePayPalSubscriptionAsync(
            paypalSubscriptionId,
            CreateReconciliation(details));
        if (result == null)
        {
            return null!;
        }

        if (sendTransitionEmails)
        {
            await SendTransitionEmailsAsync(result, baseUrl);
        }

        return result;
    }

    public async Task<PayPalCancellationResult> CancelSubscriptionAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var subscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id)
            ?? await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        if (subscription == null
            || !string.Equals(subscription.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
        {
            return new PayPalCancellationResult(false, Error: "No PayPal subscription was found.");
        }

        var wasAlreadyCancelled = string.Equals(
            subscription.Status,
            SubscriptionStatuses.Cancelled,
            StringComparison.OrdinalIgnoreCase);
        PayPalSubscriptionDetails details;
        try
        {
            details = await _payPalApi.GetSubscriptionAsync(subscription.PayPalSubscriptionId, cancellationToken);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogWarning(ex, "Cannot confirm PayPal entitlement end before cancellation for user {UserId}", user.Id);
            return new PayPalCancellationResult(
                false,
                Error: "PayPal could not confirm the subscription end date. No cancellation was submitted; please try again.");
        }

        if (details == null)
        {
            return new PayPalCancellationResult(
                false,
                Error: "PayPal could not find the subscription. No local cancellation was submitted; please contact support.");
        }

        var reconciled = await _subscriptionService.ReconcilePayPalSubscriptionAsync(
            subscription.PayPalSubscriptionId,
            CreateReconciliation(details));

        if (reconciled == null)
        {
            return new PayPalCancellationResult(false, Error: "The local subscription could not be updated.");
        }

        subscription = reconciled.Subscription;
        var providerEntitlementEnd = ResolveProviderEntitlementEnd(details, subscription);

        var providerAlreadyEnded = IsProviderEndedStatus(details.Status);
        if (!providerEntitlementEnd.HasValue && !providerAlreadyEnded)
        {
            return new PayPalCancellationResult(
                false,
                Error: "PayPal did not provide a trial or billing-period end date. No cancellation was submitted.");
        }

        if (!providerAlreadyEnded)
        {
            try
            {
                if (!await _payPalApi.CancelSubscriptionAsync(
                    subscription.PayPalSubscriptionId,
                    PayPalSubscriptionDefaults.UserCancellationReason,
                    cancellationToken))
                {
                    return new PayPalCancellationResult(false, Error: "PayPal did not accept the cancellation.");
                }
            }
            catch (PayPalSubscriptionApiException ex)
            {
                _logger.LogError(ex, "PayPal cancellation failed for user {UserId}", user.Id);
                return new PayPalCancellationResult(false, Error: "PayPal could not cancel the subscription.");
            }
        }

        if (!await _subscriptionService.CancelPayPalSubscriptionAsync(
            subscription.PayPalSubscriptionId,
            providerEntitlementEnd))
        {
            return new PayPalCancellationResult(false, Error: "The local subscription could not be cancelled.");
        }

        var updated = await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        if (!wasAlreadyCancelled)
        {
            await SendCancellationEmailAsync(user, updated, baseUrl);
        }

        return new PayPalCancellationResult(true, updated?.EndDate ?? providerEntitlementEnd);
    }

    public async Task<bool> AbandonPendingCheckoutAsync(
        ApplicationUser user,
        int? expectedPendingSubscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await _subscriptionService.GetPendingSubscriptionAsync(user.Id);

        // Background callers target a specific row they decided was stale. Everything below
        // (including the local delete) resolves "the newest pending row for this user" again, so
        // without this check a checkout started since that decision would be torn down instead.
        // This is checked before the null case on purpose: reporting success for a row we did not
        // touch would let the caller record it as cleaned up and act on that, every run, forever.
        if (expectedPendingSubscriptionId.HasValue && pending?.Id != expectedPendingSubscriptionId.Value)
        {
            _logger.LogInformation(
                "Skipping abandon of PayPal checkout {ExpectedSubscriptionId} for user {UserId}: the newest pending checkout is now {ActualSubscriptionId}",
                expectedPendingSubscriptionId.Value,
                user.Id,
                pending?.Id);
            return false;
        }

        if (pending == null)
        {
            return true;
        }

        if (!string.Equals(pending.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(pending.PayPalSubscriptionId))
        {
            return false;
        }

        PayPalSubscriptionDetails details;
        try
        {
            details = await _payPalApi.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                cancellationToken);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to verify PayPal checkout {SubscriptionId} before abandoning it for user {UserId}",
                pending.PayPalSubscriptionId,
                user.Id);
            return false;
        }

        if (details == null)
        {
            // A provider-confirmed 404 is the only case where it is safe to delete
            // without reconciling a final provider status.
            return await _subscriptionService.DeletePendingSubscriptionAsync(user.Id);
        }

        var reconciled = await ReconcileRetrievedSubscriptionAsync(
            details,
            string.Empty,
            sendTransitionEmails: false);
        if (reconciled == null)
        {
            return false;
        }

        if (IsProviderEndedStatus(reconciled.Subscription.Status))
        {
            if (!HasProviderActivationEvidence(details))
            {
                return await _subscriptionService.CancelPayPalSubscriptionAsync(
                    pending.PayPalSubscriptionId,
                    providerEntitlementEndDate: null);
            }

            return true;
        }

        if (!string.Equals(
            reconciled.Subscription.Status,
            SubscriptionStatuses.ApprovalPending,
            StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                reconciled.Subscription.Status,
                SubscriptionStatuses.Approved,
                StringComparison.OrdinalIgnoreCase))
        {
            // ACTIVE and SUSPENDED agreements are real provider subscriptions. Never
            // delete their local history merely because it was stale APPROVAL_PENDING.
            return false;
        }

        try
        {
            // Expect a 404 here rather than a 200: PayPal will not cancel an agreement the buyer
            // never approved. CancelSubscriptionAsync treats that as success - see the comment on
            // its NotFound branch - which is what lets this path close abandoned checkouts.
            if (!await _payPalApi.CancelSubscriptionAsync(
                pending.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                cancellationToken))
            {
                return false;
            }

            if (!HasProviderActivationEvidence(details))
            {
                // PayPal has accepted cancellation of an agreement that was never
                // activated or paid. Do not require an immediate follow-up GET to
                // reflect CANCELLED; PayPal may continue returning APPROVAL_PENDING
                // briefly even though the approval flow has been abandoned.
                return await _subscriptionService.CancelPayPalSubscriptionAsync(
                    pending.PayPalSubscriptionId,
                    providerEntitlementEndDate: null);
            }

            var cancelledDetails = await _payPalApi.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                cancellationToken);
            if (cancelledDetails == null)
            {
                return await _subscriptionService.DeletePendingSubscriptionAsync(user.Id);
            }

            var cancelledReconciliation = await ReconcileRetrievedSubscriptionAsync(
                cancelledDetails,
                string.Empty,
                sendTransitionEmails: false);
            if (cancelledReconciliation == null
                || !IsProviderEndedStatus(cancelledReconciliation.Subscription.Status))
            {
                return false;
            }

            if (!HasProviderActivationEvidence(cancelledDetails))
            {
                return await _subscriptionService.CancelPayPalSubscriptionAsync(
                    pending.PayPalSubscriptionId,
                    providerEntitlementEndDate: null);
            }

            return true;
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to abandon PayPal checkout {SubscriptionId} for user {UserId}",
                pending.PayPalSubscriptionId,
                user.Id);
            return false;
        }
    }

    public async Task<bool> RefreshIfNeededAsync(
        int userId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var active = await _subscriptionService.GetActiveSubscriptionAsync(userId);
        var latest = active ?? await _subscriptionService.GetLatestSubscriptionAsync(userId);
        if (latest == null
            || !string.Equals(latest.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(latest.PayPalSubscriptionId))
        {
            return false;
        }

        var boundary = DateTime.UtcNow.AddMinutes(PayPalSubscriptionDefaults.RefreshBoundaryMinutes);
        var shouldRefresh = active == null
            || (string.Equals(latest.Status, SubscriptionStatuses.Active, StringComparison.Ordinal)
                && !latest.LastPaymentDate.HasValue
                && !latest.TrialEndDate.HasValue)
            || latest.TrialEndDate <= boundary
            || latest.EndDate <= boundary
            || latest.NextBillingDate <= boundary;

        if (!shouldRefresh)
        {
            return false;
        }

        return await ReconcileSubscriptionAsync(latest.PayPalSubscriptionId, baseUrl, cancellationToken) != null;
    }

    public async Task<PayPalMismatchResolutionResult> ResolveCurrentMismatchAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var latest = await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        if (latest == null
            || !string.Equals(latest.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(latest.PayPalSubscriptionId))
        {
            return new PayPalMismatchResolutionResult(PayPalMismatchResolutionStatuses.NoAgreement);
        }

        var reconciliation = await ReconcileSubscriptionAsync(
            latest.PayPalSubscriptionId,
            baseUrl,
            cancellationToken);
        if (reconciliation == null)
        {
            var unavailableEpisode = await _anomalyService.GetOpenEpisodeAsync(latest.Id, cancellationToken);
            return new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.TemporarilyUnavailable,
                CorrelationId: unavailableEpisode?.CorrelationId,
                Error: "PayPal could not be reconciled right now. Please try again.");
        }

        var refreshed = reconciliation.Subscription;
        var active = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        if (active?.Id == refreshed.Id)
        {
            await _anomalyService.ResolveOpenEpisodeAsync(refreshed.Id, cancellationToken);
            return new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.EntitlementRestored,
                refreshed.Status,
                EntitlementEndDate: refreshed.EndDate ?? refreshed.TrialEndDate);
        }

        if (IsProviderEndedStatus(refreshed.Status))
        {
            await _anomalyService.ResolveOpenEpisodeAsync(refreshed.Id, cancellationToken);
            return new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.AgreementEnded,
                refreshed.Status);
        }

        var episode = await _anomalyService.GetOpenEpisodeAsync(refreshed.Id, cancellationToken);
        if (string.Equals(refreshed.Status, SubscriptionStatuses.Suspended, StringComparison.OrdinalIgnoreCase))
        {
            return new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.Suspended,
                refreshed.Status,
                episode?.CorrelationId);
        }

        if (string.Equals(refreshed.Status, SubscriptionStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.ActiveWithoutEntitlement,
                refreshed.Status,
                episode?.CorrelationId);
        }

        return new PayPalMismatchResolutionResult(
            PayPalMismatchResolutionStatuses.TemporarilyUnavailable,
            refreshed.Status,
            episode?.CorrelationId,
            Error: "PayPal returned a subscription state that cannot restore access yet.");
    }

    public async Task<string?> GetOpenMismatchCorrelationIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var latest = await _subscriptionService.GetLatestSubscriptionAsync(userId);
        if (latest == null
            || !string.Equals(latest.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(latest.PayPalSubscriptionId))
        {
            return null;
        }

        var episode = await _anomalyService.GetOpenEpisodeAsync(latest.Id, cancellationToken);
        return episode?.CorrelationId;
    }

    private async Task SynchronizeAnomalyEpisodeAsync(
        Subscription? localSubscription,
        PayPalSubscriptionDetails details,
        PayPalSubscriptionReconciliationResult? reconciliation,
        string? reconciliationError,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var subscription = reconciliation?.Subscription ?? localSubscription;
        if (subscription == null)
        {
            return;
        }

        var active = await _subscriptionService.GetActiveSubscriptionAsync(subscription.UserId);
        if (active?.Id == subscription.Id || IsProviderEndedStatus(details.Status))
        {
            await _anomalyService.ResolveOpenEpisodeAsync(subscription.Id, cancellationToken);
            return;
        }

        if (!IsMismatchEscalationStatus(details.Status))
        {
            // APPROVAL_PENDING/APPROVED checkouts are a normal, unbillable state: either the buyer
            // is still on the PayPal approval page or they abandoned it. Resolve rather than merely
            // skip, so an episode opened by the previous (over-broad) gate is closed instead of
            // staying attached to this row forever.
            _logger.LogDebug(
                "PayPal subscription {SubscriptionId} reported non-escalating status {ProviderStatus}; no mismatch recorded",
                subscription.Id,
                details.Status);
            await _anomalyService.ResolveOpenEpisodeAsync(subscription.Id, cancellationToken);
            return;
        }

        var user = await _userManager.FindByIdAsync(subscription.UserId.ToString(CultureInfo.InvariantCulture));
        if (user == null)
        {
            _logger.LogError(
                "Cannot record PayPal subscription anomaly for subscription {SubscriptionId} because user {UserId} was not found",
                subscription.Id,
                subscription.UserId);
            return;
        }

        await _anomalyService.RecordMismatchAsync(
            subscription,
            user,
            details,
            reconciliationError ??
                $"PayPal reported {details.Status}, but StreamTunes could not establish current streaming entitlement.",
            baseUrl,
            cancellationToken);
    }

    private async Task<bool> StopOverlappingPayPalSubscriptionAsync(
        Subscription pendingSubscription,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(pendingSubscription.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(pendingSubscription.PayPalSubscriptionId))
        {
            return false;
        }

        PayPalSubscriptionDetails details;
        try
        {
            details = await _payPalApi.GetSubscriptionAsync(
                pendingSubscription.PayPalSubscriptionId,
                cancellationToken);
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogError(
                ex,
                "Unable to verify overlapping PayPal subscription {SubscriptionId}",
                pendingSubscription.PayPalSubscriptionId);
            return false;
        }

        if (details == null)
        {
            return await _subscriptionService.DeletePendingSubscriptionAsync(pendingSubscription.UserId);
        }

        if (IsProviderEndedStatus(details.Status))
        {
            return await ReconcileRetrievedSubscriptionAsync(
                details,
                baseUrl,
                sendTransitionEmails: false) != null;
        }

        var mappedStatus = MapPayPalStatus(details.Status);
        if (!string.Equals(mappedStatus, SubscriptionStatuses.ApprovalPending, StringComparison.Ordinal)
            && !string.Equals(mappedStatus, SubscriptionStatuses.Approved, StringComparison.Ordinal)
            && !string.Equals(mappedStatus, SubscriptionStatuses.Active, StringComparison.Ordinal)
            && !string.Equals(mappedStatus, SubscriptionStatuses.Suspended, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!await _payPalApi.CancelSubscriptionAsync(
                pendingSubscription.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                cancellationToken))
            {
                return false;
            }
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogError(
                ex,
                "Unable to stop overlapping PayPal subscription {SubscriptionId}",
                pendingSubscription.PayPalSubscriptionId);
            return false;
        }

        try
        {
            // Capture the provider's final state when available. If this refresh fails
            // after PayPal accepted cancellation, the pre-cancellation details still
            // preserve evidence that an activation or payment occurred.
            var refreshedDetails = await _payPalApi.GetSubscriptionAsync(
                pendingSubscription.PayPalSubscriptionId,
                cancellationToken);
            if (refreshedDetails != null)
            {
                details = refreshedDetails;
            }
        }
        catch (PayPalSubscriptionApiException ex)
        {
            _logger.LogWarning(
                ex,
                "PayPal accepted cancellation of overlapping subscription {SubscriptionId}, but its final state could not be refreshed",
                pendingSubscription.PayPalSubscriptionId);
        }

        var reconciled = await ReconcileRetrievedSubscriptionAsync(
            details,
            baseUrl,
            sendTransitionEmails: false);
        if (reconciled == null)
        {
            return false;
        }

        var entitlementEnd = ResolveProviderEntitlementEnd(details, reconciled.Subscription);
        return await _subscriptionService.CancelPayPalSubscriptionAsync(
            pendingSubscription.PayPalSubscriptionId,
            entitlementEnd);
    }

    private async Task<string?> ClearAbandonedPendingCheckoutAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var pending = await _subscriptionService.GetPendingSubscriptionAsync(user.Id);
        if (pending == null
            || !string.Equals(pending.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(pending.PayPalSubscriptionId))
        {
            return null;
        }

        var reconciled = await ReconcileSubscriptionAsync(
            pending.PayPalSubscriptionId,
            baseUrl,
            cancellationToken);
        if (reconciled == null)
        {
            return "The earlier PayPal checkout could not be verified, so a second subscription was not started. Please try again later.";
        }

        var reconciledStatus = reconciled?.Subscription.Status;

        if (string.Equals(reconciledStatus, SubscriptionStatuses.Active, StringComparison.Ordinal)
            || string.Equals(reconciledStatus, SubscriptionStatuses.Suspended, StringComparison.Ordinal))
        {
            return "An existing PayPal subscription is already active or awaiting payment recovery. No overlapping subscription was started.";
        }

        if (IsProviderEndedStatus(reconciledStatus))
        {
            // Reconciliation has already changed the row out of APPROVAL_PENDING.
            return null;
        }

        if (!string.Equals(
            reconciledStatus,
            SubscriptionStatuses.ApprovalPending,
            StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                reconciledStatus,
                SubscriptionStatuses.Approved,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The earlier PayPal checkout is in an unresolved provider state, so a second subscription was not started. Please try again later.";
        }

        if (!await AbandonPendingCheckoutAsync(user, cancellationToken: cancellationToken))
        {
            return "The earlier PayPal checkout could not be closed, so a second subscription was not started. Please try again later.";
        }

        return null;
    }

    private static bool PlanMatchesQuote(PayPalPlan plan, PayPalWebOfferQuote quote)
    {
        return string.Equals(plan.Status, PayPalPlanStatuses.Active, StringComparison.Ordinal)
            && plan.RegularBillingCycle != null
            && plan.BillingCycles.Count(cycle => string.Equals(
                cycle.TenureType,
                PayPalBillingTenureTypes.Regular,
                StringComparison.Ordinal)) == 1
            && plan.RegularBillingCycle.TotalCycles == 0
            && plan.SetupFee == decimal.Zero
            && plan.TaxPercentage == decimal.Zero
            && plan.RegularPrice == quote.RegularPrice
            && string.Equals(plan.CurrencyCode, quote.CurrencyCode, StringComparison.Ordinal)
            && string.Equals(plan.IntervalUnit, quote.IntervalUnit, StringComparison.Ordinal)
            && plan.IntervalCount == quote.IntervalCount
            && plan.HasTrial == quote.HasFreeTrial
            && (!quote.HasFreeTrial
                || plan.TrialBillingCycles.Count == plan.FreeTrialBillingCycles.Count)
            && plan.TrialDays == quote.TrialDays;
    }

    private static PayPalSubscriptionReconciliation CreateReconciliation(PayPalSubscriptionDetails details)
    {
        var hasUnconvertedTrial = HasActivatedUnconvertedTrial(details);
        var hasProviderActivation = HasProviderActivationEvidence(details);
        var providerTrialEnd = hasUnconvertedTrial
            ? details.TrialEndDate ?? details.NextBillingDate
            : details.LastPaymentDate.HasValue && details.Plan?.HasFreeTrial == true
                ? details.TrialEndDate
                : null;
        return new PayPalSubscriptionReconciliation
        {
            Status = MapPayPalStatus(details.Status),
            PlanId = details.PlanId,
            ProviderStartDate = details.StartDate,
            TrialStartDate = providerTrialEnd.HasValue ? details.StartDate : null,
            TrialEndDate = providerTrialEnd,
            LastPaymentDate = details.LastPaymentDate,
            NextBillingDate = hasProviderActivation ? details.NextBillingDate : null,
            FailedPaymentsCount = details.FailedPaymentsCount,
            RegularIntervalUnit = details.Plan?.IntervalUnit,
            RegularIntervalCount = details.Plan?.IntervalCount ?? 0,
            MonthlyPrice = details.Plan?.RegularPrice > 0 ? details.Plan.RegularPrice : null,
            CurrencyCode = details.Plan?.CurrencyCode
        };
    }

    private static bool HasActivatedUnconvertedTrial(PayPalSubscriptionDetails details)
    {
        var providerStatus = details.Status?.ToUpperInvariant();
        var providerActivationOccurred = providerStatus != SubscriptionStatuses.ApprovalPending
            && providerStatus != SubscriptionStatuses.Approved;
        var hasProviderTrialEvidence = details.IsInTrial
            || details.CycleExecutions.Any(execution => string.Equals(
                execution.TenureType,
                PayPalBillingTenureTypes.Trial,
                StringComparison.Ordinal))
            || (details.HasBillingInfo && details.TrialEndDate.HasValue);

        return providerActivationOccurred
            && hasProviderTrialEvidence
            && details.Plan?.HasFreeTrial == true
            && !details.LastPaymentDate.HasValue
            && (details.TrialEndDate.HasValue || details.NextBillingDate.HasValue);
    }

    private static bool HasProviderActivationEvidence(PayPalSubscriptionDetails details)
    {
        return details.LastPaymentDate.HasValue || HasActivatedUnconvertedTrial(details);
    }

    private static DateTime? ResolveProviderEntitlementEnd(
        PayPalSubscriptionDetails details,
        Subscription subscription)
    {
        if (string.Equals(details.Status, SubscriptionStatuses.Expired, StringComparison.OrdinalIgnoreCase))
        {
            return details.NextBillingDate
                ?? subscription.NextBillingDate
                ?? subscription.EndDate
                ?? subscription.TrialEndDate;
        }

        var hasActivationEvidence = HasProviderActivationEvidence(details)
            || subscription.LastPaymentDate.HasValue
            || subscription.TrialStartDate.HasValue
            || subscription.TrialEndDate.HasValue;
        if (!hasActivationEvidence)
        {
            return null;
        }

        if (HasActivatedUnconvertedTrial(details))
        {
            return details.TrialEndDate
                ?? details.NextBillingDate
                ?? subscription.TrialEndDate
                ?? subscription.EndDate;
        }

        return details.NextBillingDate
            ?? subscription.NextBillingDate
            ?? subscription.EndDate
            ?? subscription.TrialEndDate;
    }

    private async Task SendTransitionEmailsAsync(
        PayPalSubscriptionReconciliationResult result,
        string baseUrl)
    {
        var subscription = result.Subscription;
        var user = await _userManager.FindByIdAsync(subscription.UserId.ToString());
        if (user == null)
        {
            return;
        }

        if (result.ShouldSendTrialActivationEmail)
        {
            if (await _confirmationEmailService.SendTrialStartedAsync(user, subscription, ResolveBaseUrl(baseUrl)))
            {
                await _subscriptionService.MarkTrialActivationEmailSentAsync(subscription.Id);
            }
        }
        else if (result.ShouldSendTrialConversionEmail)
        {
            if (await _confirmationEmailService.SendTrialConvertedAsync(user, subscription, ResolveBaseUrl(baseUrl)))
            {
                await _subscriptionService.MarkTrialConversionEmailSentAsync(subscription.Id);
            }
        }
        else if ((result.BecameActive || result.BecamePaid)
            && !subscription.TrialEndDate.HasValue
            && string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal))
        {
            await _confirmationEmailService.SendConfirmationAsync(user, subscription, ResolveBaseUrl(baseUrl));
        }

        if (!string.Equals(result.PreviousStatus, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
            && string.Equals(subscription.Status, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            await SendCancellationEmailAsync(user, subscription, baseUrl);
        }
    }

    private async Task SendCancellationEmailAsync(
        ApplicationUser user,
        Subscription? subscription,
        string baseUrl)
    {
        if (subscription == null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        await _accountEmailService.SendSubscriptionCancelledEmailAsync(
            user.Email,
            user.UserName ?? user.Email,
            subscription.EndDate,
            subscription.BillingSource,
            user.TimeZoneId,
            ResolveBaseUrl(baseUrl));
    }

    private string ResolveBaseUrl(string fallbackBaseUrl)
    {
        var configured = _configuration["PayPal:ReturnBaseUrl"];
        return (string.IsNullOrWhiteSpace(configured) ? fallbackBaseUrl : configured).TrimEnd('/');
    }

    private static bool IsProviderEndedStatus(string? status)
    {
        return IsProviderCancelledStatus(status)
            || string.Equals(status, SubscriptionStatuses.Expired, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPotentiallyBillableStatus(string? status)
    {
        return string.Equals(status, SubscriptionStatuses.ApprovalPending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubscriptionStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubscriptionStatuses.Active, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubscriptionStatuses.Suspended, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Provider statuses worth escalating an entitlement mismatch to the user and admin.
    /// Deliberately narrower than <see cref="IsPotentiallyBillableStatus"/>: APPROVAL_PENDING and
    /// APPROVED mean the buyer never completed approval, so PayPal cannot charge them and there is
    /// no double-billing risk to warn about. Keep this aligned with the statuses that actually
    /// block a new subscription (CreateSubscriptionUnderLockAsync and
    /// ManageAccount.HasUnresolvedPayPalAgreement).
    /// </summary>
    private static bool IsMismatchEscalationStatus(string? status)
    {
        return string.Equals(status, SubscriptionStatuses.Active, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubscriptionStatuses.Suspended, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderCancelledStatus(string? status)
    {
        return string.Equals(status, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubscriptionStatuses.Canceled, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapPayPalStatus(string status)
    {
        return status?.ToUpperInvariant() switch
        {
            SubscriptionStatuses.Active => SubscriptionStatuses.Active,
            SubscriptionStatuses.ApprovalPending => SubscriptionStatuses.ApprovalPending,
            SubscriptionStatuses.Approved => SubscriptionStatuses.ApprovalPending,
            SubscriptionStatuses.Cancelled or SubscriptionStatuses.Canceled => SubscriptionStatuses.Cancelled,
            SubscriptionStatuses.Suspended => SubscriptionStatuses.Suspended,
            SubscriptionStatuses.Expired => SubscriptionStatuses.Expired,
            _ => SubscriptionStatuses.Suspended
        };
    }
}
