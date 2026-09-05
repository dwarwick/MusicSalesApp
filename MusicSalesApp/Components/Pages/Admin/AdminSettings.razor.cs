using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Globalization;

#nullable enable

namespace MusicSalesApp.Components.Pages.Admin;

public class AdminSettingsModel : BlazorBase
{
    [Inject] private IHubContext<MaintenanceHub> MaintenanceHubContext { get; set; } = default!;
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected string? _successMessage = null;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    protected bool _hasLoadedData = false;

    // Settings fields
    protected decimal? _streamPayRateDisplay = null; // Display as per 1000 streams (e.g., 5.00)
    protected decimal? _originalStreamPayRateDisplay = null;
    protected int _streamQualifyingSeconds = 30;
    protected int _originalStreamQualifyingSeconds = 30;
    protected int _maxAudioUploadSizeMB = 100;
    protected int _originalMaxAudioUploadSizeMB = 100;
    protected int _maxImageUploadSizeMB = 20;
    protected int _originalMaxImageUploadSizeMB = 20;

    // Held as a percentage because that is how it is read everywhere else - the creator's lyrics
    // dialog reports "61% confidence" - while the service stores it as 0-1. Asking an admin to
    // compare 0.61 against a number the UI showed them as 61% is a needless conversion to get wrong.
    protected decimal _lyricsConfidenceThresholdPercent = 70m;
    protected decimal _originalLyricsConfidenceThresholdPercent = 70m;

    protected bool _lyricsCompletionEmailsEnabled = true;
    protected bool _originalLyricsCompletionEmailsEnabled = true;

    // Defaults false to match AppSettingsService: this one moves the number creator payouts are
    // calculated from, so a load failure must never present it as on.
    protected bool _reducedStreamQualifyingEnabled;
    protected bool _originalReducedStreamQualifyingEnabled;

    // Defaults false to match AppSettingsService, so a load failure can never present the feature as
    // on when it is not - or invite an admin to "turn off" something already off.
    protected bool _directToStorageUploadEnabled;
    protected bool _originalDirectToStorageUploadEnabled;
    protected bool _pushNotificationsEnabled;
    protected bool _originalPushNotificationsEnabled;
    protected string _appVersion = string.Empty;
    protected string _originalAppVersion = string.Empty;

    // Admin notification settings
    protected bool _notifyRegistration = true;
    protected bool _notifyEmailConfirmed = true;
    protected bool _notifyTaxFormCompleted = true;
    protected bool _notifyCreatorStatusGained = true;
    protected bool _notifyCreatorStatusLost = true;
    protected bool _notifyUploadCompleted = true;
    protected bool _notifySongRenamed = true;
    protected bool _notifySongArtUpdated = true;
    protected bool _notifyLyricsChanged = true;
    protected bool _notifyTipFraudPrevented = true;

    // Original values for change tracking
    protected bool _originalNotifyRegistration = true;
    protected bool _originalNotifyEmailConfirmed = true;
    protected bool _originalNotifyTaxFormCompleted = true;
    protected bool _originalNotifyCreatorStatusGained = true;
    protected bool _originalNotifyCreatorStatusLost = true;
    protected bool _originalNotifyUploadCompleted = true;
    protected bool _originalNotifySongRenamed = true;
    protected bool _originalNotifySongArtUpdated = true;
    protected bool _originalNotifyLyricsChanged = true;
    protected bool _originalNotifyTipFraudPrevented = true;
    protected bool _isSavingNotifications = false;

    // Tax Bandits maintenance window fields
    protected DateTime? _maintenanceStartEastern = null;
    protected DateTime? _originalMaintenanceStartEastern = null;
    protected DateTime? _maintenanceEndEastern = null;
    protected DateTime? _originalMaintenanceEndEastern = null;
    protected bool _isSavingMaintenance = false;
    protected string? _maintenanceSuccessMessage = null;
    protected List<string> _maintenanceValidationErrors = new();

    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo PacificTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    // Site maintenance window fields
    protected DateTime? _siteMaintenanceStartPacific = null;
    protected DateTime? _originalSiteMaintenanceStartPacific = null;
    protected DateTime? _siteMaintenanceEndPacific = null;
    protected DateTime? _originalSiteMaintenanceEndPacific = null;
    protected bool _isSavingSiteMaintenance = false;
    protected string? _siteMaintenanceSuccessMessage = null;
    protected List<string> _siteMaintenanceValidationErrors = new();

    // PayPal web subscription offer fields
    protected IReadOnlyList<PayPalPlanOption> _payPalPlanOptions = Array.Empty<PayPalPlanOption>();
    protected PayPalWebSubscriptionOffer? _savedPayPalWebOffer;
    protected string? _selectedPrimaryPayPalPlanId;
    protected string? _selectedResubscriberPayPalPlanId;
    protected bool _isRefreshingPayPalPlans;
    protected bool _isSavingPayPalOffer;
    protected string? _payPalOfferErrorMessage;
    protected string? _payPalOfferSuccessMessage;
    protected List<string> _payPalOfferValidationErrors = new();

    protected bool _hasChanges => _streamPayRateDisplay != _originalStreamPayRateDisplay
                                   || _streamQualifyingSeconds != _originalStreamQualifyingSeconds
                                   || _maxAudioUploadSizeMB != _originalMaxAudioUploadSizeMB
                                   || _maxImageUploadSizeMB != _originalMaxImageUploadSizeMB
                                   || _reducedStreamQualifyingEnabled != _originalReducedStreamQualifyingEnabled
                                   || _directToStorageUploadEnabled != _originalDirectToStorageUploadEnabled
                                   || _pushNotificationsEnabled != _originalPushNotificationsEnabled
                                   || _lyricsConfidenceThresholdPercent != _originalLyricsConfidenceThresholdPercent
                                   || _lyricsCompletionEmailsEnabled != _originalLyricsCompletionEmailsEnabled
                                   || _appVersion != _originalAppVersion;

    protected bool _hasNotificationChanges => _notifyRegistration != _originalNotifyRegistration
                                             || _notifyEmailConfirmed != _originalNotifyEmailConfirmed
                                             || _notifyTaxFormCompleted != _originalNotifyTaxFormCompleted
                                             || _notifyCreatorStatusGained != _originalNotifyCreatorStatusGained
                                             || _notifyCreatorStatusLost != _originalNotifyCreatorStatusLost
                                             || _notifyUploadCompleted != _originalNotifyUploadCompleted
                                             || _notifySongRenamed != _originalNotifySongRenamed
                                             || _notifySongArtUpdated != _originalNotifySongArtUpdated
                                             || _notifyLyricsChanged != _originalNotifyLyricsChanged
                                             || _notifyTipFraudPrevented != _originalNotifyTipFraudPrevented;

    protected bool _hasMaintenanceChanges => _maintenanceStartEastern != _originalMaintenanceStartEastern
                                              || _maintenanceEndEastern != _originalMaintenanceEndEastern;

    protected bool _hasSiteMaintenanceChanges => _siteMaintenanceStartPacific != _originalSiteMaintenanceStartPacific
                                                  || _siteMaintenanceEndPacific != _originalSiteMaintenanceEndPacific;

    protected bool _hasPayPalOfferChanges =>
        !string.Equals(_selectedPrimaryPayPalPlanId, _savedPayPalWebOffer?.PrimaryPlan.Id, StringComparison.Ordinal)
        || !string.Equals(
            _selectedResubscriberPayPalPlanId,
            _savedPayPalWebOffer?.ResubscriberPlan?.Id,
            StringComparison.Ordinal);

    protected PayPalPlan? SelectedPrimaryPayPalPlan => FindPayPalPlan(_selectedPrimaryPayPalPlanId);

    protected PayPalPlan? SelectedResubscriberPayPalPlan => FindPayPalPlan(_selectedResubscriberPayPalPlanId);

    protected bool SelectedPrimaryRequiresCompanion =>
        SelectedPrimaryPayPalPlan?.HasTrial == true
        || (string.Equals(
                _selectedPrimaryPayPalPlanId,
                _savedPayPalWebOffer?.PrimaryPlan.Id,
                StringComparison.Ordinal)
            && _savedPayPalWebOffer?.PrimaryPlan.HasFreeTrial == true);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load settings: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadSettingsAsync()
    {
        // Convert stream pay rate from per-stream to per-1000-streams for display
        var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
        _streamPayRateDisplay = streamPayRate * 1000;
        _originalStreamPayRateDisplay = _streamPayRateDisplay;

        _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
        _originalStreamQualifyingSeconds = _streamQualifyingSeconds;

        _maxAudioUploadSizeMB = await AppSettingsService.GetMaxAudioUploadSizeMBAsync();
        _originalMaxAudioUploadSizeMB = _maxAudioUploadSizeMB;

        _maxImageUploadSizeMB = await AppSettingsService.GetMaxImageUploadSizeMBAsync();
        _originalMaxImageUploadSizeMB = _maxImageUploadSizeMB;

        _reducedStreamQualifyingEnabled = await AppSettingsService.IsReducedStreamQualifyingEnabledAsync();
        _originalReducedStreamQualifyingEnabled = _reducedStreamQualifyingEnabled;

        _directToStorageUploadEnabled = await AppSettingsService.IsDirectToStorageUploadEnabledAsync();
        _originalDirectToStorageUploadEnabled = _directToStorageUploadEnabled;

        _pushNotificationsEnabled = await AppSettingsService.IsPushNotificationsEnabledAsync();
        _originalPushNotificationsEnabled = _pushNotificationsEnabled;

        _lyricsConfidenceThresholdPercent =
            (decimal)(await AppSettingsService.GetLyricsConfidenceThresholdAsync()) * 100m;
        _originalLyricsConfidenceThresholdPercent = _lyricsConfidenceThresholdPercent;

        _lyricsCompletionEmailsEnabled = await AppSettingsService.GetLyricsCompletionEmailsEnabledAsync();
        _originalLyricsCompletionEmailsEnabled = _lyricsCompletionEmailsEnabled;

        _appVersion = await AppSettingsService.GetAppVersionAsync() ?? string.Empty;
        _originalAppVersion = _appVersion;

        _savedPayPalWebOffer = await AppSettingsService.GetPayPalWebSubscriptionOfferAsync();
        RestoreSavedPayPalOfferSelection();
        await RefreshPayPalPlansAsync();

        // Load admin notification settings
        _notifyRegistration = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyRegistrationKey);
        _originalNotifyRegistration = _notifyRegistration;

        _notifyEmailConfirmed = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyEmailConfirmedKey);
        _originalNotifyEmailConfirmed = _notifyEmailConfirmed;

        _notifyTaxFormCompleted = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyTaxFormCompletedKey);
        _originalNotifyTaxFormCompleted = _notifyTaxFormCompleted;

        _notifyCreatorStatusGained = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyCreatorStatusGainedKey);
        _originalNotifyCreatorStatusGained = _notifyCreatorStatusGained;

        _notifyCreatorStatusLost = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyCreatorStatusLostKey);
        _originalNotifyCreatorStatusLost = _notifyCreatorStatusLost;

        _notifyUploadCompleted = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyUploadCompletedKey);
        _originalNotifyUploadCompleted = _notifyUploadCompleted;

        _notifySongRenamed = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifySongRenamedKey);
        _originalNotifySongRenamed = _notifySongRenamed;

        _notifySongArtUpdated = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifySongArtUpdatedKey);
        _originalNotifySongArtUpdated = _notifySongArtUpdated;
        _notifyLyricsChanged = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyLyricsChangedKey);
        _originalNotifyLyricsChanged = _notifyLyricsChanged;

        _notifyTipFraudPrevented = await AdminNotificationService.IsNotificationEnabledAsync(Services.AdminNotificationService.NotifyTipFraudPreventedKey);
        _originalNotifyTipFraudPrevented = _notifyTipFraudPrevented;

        // Load Tax Bandits maintenance window settings
        var startUtc = await AppSettingsService.GetTaxBanditsMaintenanceStartUtcAsync();
        if (startUtc.HasValue && startUtc.Value != DateTime.MinValue)
        {
            _maintenanceStartEastern = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(startUtc.Value, DateTimeKind.Utc), EasternTimeZone);
        }
        _originalMaintenanceStartEastern = _maintenanceStartEastern;

        var endUtc = await AppSettingsService.GetTaxBanditsMaintenanceEndUtcAsync();
        if (endUtc.HasValue && endUtc.Value != DateTime.MinValue)
        {
            _maintenanceEndEastern = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(endUtc.Value, DateTimeKind.Utc), EasternTimeZone);
        }
        _originalMaintenanceEndEastern = _maintenanceEndEastern;

        // Load site maintenance window settings
        var siteStartUtc = await AppSettingsService.GetSiteMaintenanceStartUtcAsync();
        if (siteStartUtc.HasValue && siteStartUtc.Value != DateTime.MinValue)
        {
            _siteMaintenanceStartPacific = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(siteStartUtc.Value, DateTimeKind.Utc), PacificTimeZone);
        }
        _originalSiteMaintenanceStartPacific = _siteMaintenanceStartPacific;

        var siteEndUtc = await AppSettingsService.GetSiteMaintenanceEndUtcAsync();
        if (siteEndUtc.HasValue && siteEndUtc.Value != DateTime.MinValue)
        {
            _siteMaintenanceEndPacific = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(siteEndUtc.Value, DateTimeKind.Utc), PacificTimeZone);
        }
        _originalSiteMaintenanceEndPacific = _siteMaintenanceEndPacific;
    }

    protected async Task RefreshPayPalPlansAsync()
    {
        _isRefreshingPayPalPlans = true;
        _payPalOfferErrorMessage = null;
        _payPalOfferSuccessMessage = null;

        try
        {
            var plans = (await PayPalSubscriptionApiService.GetActivePlansAsync()).ToList();
            var selectedPlanIds = new[]
                {
                    _selectedPrimaryPayPalPlanId,
                    _selectedResubscriberPayPalPlanId,
                    _savedPayPalWebOffer?.PrimaryPlan.Id,
                    _savedPayPalWebOffer?.ResubscriberPlan?.Id
                }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Cast<string>();

            foreach (var selectedPlanId in selectedPlanIds)
            {
                if (plans.Any(plan => string.Equals(plan.Id, selectedPlanId, StringComparison.Ordinal)))
                {
                    continue;
                }

                var selectedPlan = await PayPalSubscriptionApiService.GetPlanAsync(selectedPlanId);
                if (selectedPlan != null)
                {
                    plans.Add(selectedPlan);
                }
            }

            _payPalPlanOptions = plans
                .GroupBy(plan => plan.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.Id, StringComparer.Ordinal)
                .Select(plan => new PayPalPlanOption(plan))
                .ToList();
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Failed to refresh PayPal subscription plans for Admin Settings.");
        }
        catch (Exception ex)
        {
            _payPalOfferErrorMessage =
                "PayPal plans could not be refreshed. The saved offer and current selections were preserved; retry when PayPal is available.";
            Logger.LogError(ex, "Failed to refresh PayPal subscription plans for Admin Settings.");
        }
        finally
        {
            _isRefreshingPayPalPlans = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void CancelPayPalOfferChanges()
    {
        RestoreSavedPayPalOfferSelection();
        _payPalOfferValidationErrors.Clear();
        _payPalOfferErrorMessage = null;
        _payPalOfferSuccessMessage = null;
        StateHasChanged();
    }

    protected async Task SavePayPalOfferAsync()
    {
        _isSavingPayPalOffer = true;
        _payPalOfferValidationErrors.Clear();
        _payPalOfferErrorMessage = null;
        _payPalOfferSuccessMessage = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_selectedPrimaryPayPalPlanId))
            {
                _payPalOfferValidationErrors.Add("Select the PayPal plan offered to first-time subscribers.");
                return;
            }

            var primaryPlan = await PayPalSubscriptionApiService.GetPlanAsync(_selectedPrimaryPayPalPlanId);
            ValidatePrimaryPayPalPlan(primaryPlan);

            PayPalPlan? resubscriberPlan = null;
            if (primaryPlan?.HasTrial == true)
            {
                if (string.IsNullOrWhiteSpace(_selectedResubscriberPayPalPlanId))
                {
                    _payPalOfferValidationErrors.Add(
                        "Select the matching no-trial plan offered to returning subscribers.");
                }
                else
                {
                    resubscriberPlan = await PayPalSubscriptionApiService.GetPlanAsync(
                        _selectedResubscriberPayPalPlanId);
                    ValidateResubscriberPayPalPlan(primaryPlan, resubscriberPlan);
                }
            }

            if (_payPalOfferValidationErrors.Count > 0 || primaryPlan == null)
            {
                return;
            }

            var offer = new PayPalWebSubscriptionOffer
            {
                PrimaryPlan = CreatePayPalPlanSnapshot(primaryPlan),
                ResubscriberPlan = resubscriberPlan == null
                    ? null
                    : CreatePayPalPlanSnapshot(resubscriberPlan)
            };

            _savedPayPalWebOffer = await AppSettingsService.SetPayPalWebSubscriptionOfferAsync(offer);
            _selectedPrimaryPayPalPlanId = _savedPayPalWebOffer.PrimaryPlan.Id;
            _selectedResubscriberPayPalPlanId = _savedPayPalWebOffer.ResubscriberPlan?.Id;
            MergePayPalPlanOptions(primaryPlan, resubscriberPlan);
            _payPalOfferSuccessMessage =
                $"PayPal web subscription offer version {_savedPayPalWebOffer.Version} was saved.";

            Logger.LogInformation(
                "Saved PayPal web offer version {Version}: primary {PrimaryPlanId}, re-subscriber {ResubscriberPlanId}.",
                _savedPayPalWebOffer.Version,
                _savedPayPalWebOffer.PrimaryPlan.Id,
                _savedPayPalWebOffer.ResubscriberPlan?.Id);
        }
        catch (Exception ex)
        {
            _payPalOfferErrorMessage =
                "The PayPal web offer could not be saved. The previously saved offer is still in effect.";
            Logger.LogError(ex, "Failed to validate or save the PayPal web subscription offer.");
        }
        finally
        {
            _isSavingPayPalOffer = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected static string FormatPayPalPlanTerms(PayPalPlan plan)
    {
        var regularTerms = FormatRegularTerms(
            plan.RegularPrice,
            plan.CurrencyCode,
            plan.IntervalUnit,
            plan.IntervalCount);

        var trialDays = plan.TrialDays.GetValueOrDefault();
        if (plan.HasTrial && (!plan.HasFreeTrial || trialDays <= 0))
        {
            return $"unsupported trial cadence, then {regularTerms}";
        }

        return plan.HasFreeTrial && trialDays > 0
            ? $"{trialDays} {PluralizeDay(trialDays)} free, then {regularTerms}"
            : regularTerms;
    }

    protected static string FormatPayPalPlanTerms(PayPalWebPlanSnapshot plan)
    {
        var regularTerms = FormatRegularTerms(
            plan.RegularPrice,
            plan.CurrencyCode,
            plan.IntervalUnit,
            plan.IntervalCount);

        return plan.TrialDays > 0
            ? $"{plan.TrialDays} {PluralizeDay(plan.TrialDays.Value)} free, then {regularTerms}"
            : regularTerms;
    }

    private void ValidatePrimaryPayPalPlan(PayPalPlan? plan)
    {
        if (plan == null)
        {
            _payPalOfferValidationErrors.Add("The selected first-time subscriber plan no longer exists in PayPal.");
            return;
        }

        if (!string.Equals(plan.Status, PayPalPlanStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            _payPalOfferValidationErrors.Add("The selected first-time subscriber plan must be active in PayPal.");
        }

        if (!AllowsAcceleratedSandboxCadence && !IsMonthlyPlan(plan))
        {
            _payPalOfferValidationErrors.Add(
                "The selected first-time subscriber plan must bill once per month outside PayPal sandbox mode.");
        }

        if (!HasSingleRegularBillingCycle(plan))
        {
            _payPalOfferValidationErrors.Add("The selected first-time subscriber plan must have exactly one regular billing cycle.");
        }

        else if (plan.RegularBillingCycle.TotalCycles != 0)
        {
            _payPalOfferValidationErrors.Add("The selected first-time subscriber plan must have an unlimited regular billing cycle.");
        }

        ValidateNoEnrollmentCharges(plan, "first-time subscriber");

        if (plan.RegularPrice <= 0 || string.IsNullOrWhiteSpace(plan.CurrencyCode))
        {
            _payPalOfferValidationErrors.Add("The selected plan must have a valid regular price and currency.");
        }

        if (plan.HasTrial
            && (plan.TrialBillingCycles.Count != plan.FreeTrialBillingCycles.Count
                || !plan.TrialDays.HasValue
                || plan.TrialDays.Value <= 0))
        {
            _payPalOfferValidationErrors.Add(
                "The selected trial plan must contain only free, whole-day trial cycles with a valid duration.");
        }
    }

    private void ValidateResubscriberPayPalPlan(PayPalPlan primaryPlan, PayPalPlan? resubscriberPlan)
    {
        if (resubscriberPlan == null)
        {
            _payPalOfferValidationErrors.Add("The selected returning-subscriber plan no longer exists in PayPal.");
            return;
        }

        if (string.Equals(primaryPlan.Id, resubscriberPlan.Id, StringComparison.Ordinal))
        {
            _payPalOfferValidationErrors.Add("The returning-subscriber plan must be a separate no-trial plan.");
        }

        if (!string.Equals(resubscriberPlan.Status, PayPalPlanStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            _payPalOfferValidationErrors.Add("The selected returning-subscriber plan must be active in PayPal.");
        }

        if (resubscriberPlan.HasTrial)
        {
            _payPalOfferValidationErrors.Add("The returning-subscriber plan cannot include any trial billing cycle.");
        }

        if (!AllowsAcceleratedSandboxCadence && !IsMonthlyPlan(resubscriberPlan))
        {
            _payPalOfferValidationErrors.Add(
                "The returning-subscriber plan must bill once per month outside PayPal sandbox mode.");
        }

        if (!HasSingleRegularBillingCycle(resubscriberPlan))
        {
            _payPalOfferValidationErrors.Add("The returning-subscriber plan must have exactly one regular billing cycle.");
        }

        else if (resubscriberPlan.RegularBillingCycle.TotalCycles != 0)
        {
            _payPalOfferValidationErrors.Add("The returning-subscriber plan must have an unlimited regular billing cycle.");
        }

        ValidateNoEnrollmentCharges(resubscriberPlan, "returning-subscriber");

        if (resubscriberPlan.RegularPrice != primaryPlan.RegularPrice
            || !string.Equals(
                resubscriberPlan.CurrencyCode,
                primaryPlan.CurrencyCode,
                StringComparison.OrdinalIgnoreCase)
            || resubscriberPlan.IntervalCount != primaryPlan.IntervalCount
            || !string.Equals(
                resubscriberPlan.IntervalUnit,
                primaryPlan.IntervalUnit,
                StringComparison.OrdinalIgnoreCase))
        {
            _payPalOfferValidationErrors.Add(
                "The returning-subscriber plan must match the primary plan's regular price, currency, and billing cadence.");
        }
    }

    private bool AllowsAcceleratedSandboxCadence
        => bool.TryParse(
               Configuration[PayPalConfigurationKeys.SandboxMode],
               out var sandboxMode)
           && sandboxMode;

    private static bool IsMonthlyPlan(PayPalPlan plan)
    {
        return plan.IntervalCount == 1
               && string.Equals(
                   plan.IntervalUnit,
                   PayPalBillingIntervals.Month,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSingleRegularBillingCycle(PayPalPlan plan)
    {
        return plan.BillingCycles.Count(cycle => string.Equals(
            cycle.TenureType,
            PayPalBillingTenureTypes.Regular,
            StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private void ValidateNoEnrollmentCharges(PayPalPlan plan, string planLabel)
    {
        if (plan.SetupFee != decimal.Zero)
        {
            _payPalOfferValidationErrors.Add($"The selected {planLabel} plan cannot charge a setup fee.");
        }

        if (plan.TaxPercentage != decimal.Zero)
        {
            _payPalOfferValidationErrors.Add($"The selected {planLabel} plan cannot add a PayPal plan tax that is not shown in the offer.");
        }
    }

    private static PayPalWebPlanSnapshot CreatePayPalPlanSnapshot(PayPalPlan plan)
    {
        return new PayPalWebPlanSnapshot
        {
            Id = plan.Id,
            Name = plan.Name,
            Status = plan.Status,
            RegularPrice = plan.RegularPrice,
            CurrencyCode = plan.CurrencyCode,
            IntervalUnit = plan.IntervalUnit,
            IntervalCount = plan.IntervalCount,
            TrialDays = plan.HasFreeTrial ? plan.TrialDays : null
        };
    }

    private PayPalPlan? FindPayPalPlan(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return null;
        }

        return _payPalPlanOptions
            .FirstOrDefault(option => string.Equals(option.Id, planId, StringComparison.Ordinal))
            ?.Plan;
    }

    private void RestoreSavedPayPalOfferSelection()
    {
        _selectedPrimaryPayPalPlanId = _savedPayPalWebOffer?.PrimaryPlan.Id;
        _selectedResubscriberPayPalPlanId = _savedPayPalWebOffer?.ResubscriberPlan?.Id;
    }

    private void MergePayPalPlanOptions(params PayPalPlan?[] plans)
    {
        _payPalPlanOptions = _payPalPlanOptions
            .Select(option => option.Plan)
            .Concat(plans.OfType<PayPalPlan>())
            .GroupBy(plan => plan.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id, StringComparer.Ordinal)
            .Select(plan => new PayPalPlanOption(plan))
            .ToList();
    }

    private static string FormatRegularTerms(
        decimal price,
        string currencyCode,
        string intervalUnit,
        int intervalCount)
    {
        if (price <= 0
            || string.IsNullOrWhiteSpace(currencyCode)
            || string.IsNullOrWhiteSpace(intervalUnit)
            || intervalCount <= 0)
        {
            return "billing terms unavailable";
        }

        var priceText = string.Equals(
                currencyCode,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                StringComparison.OrdinalIgnoreCase)
            ? $"${price.ToString("0.00", CultureInfo.InvariantCulture)} {PayPalSubscriptionDefaults.UsdCurrencyCode}"
            : $"{price.ToString("0.00", CultureInfo.InvariantCulture)} {currencyCode.ToUpperInvariant()}";
        var interval = intervalUnit.ToLowerInvariant();

        return intervalCount == 1
            ? $"{priceText}/{interval}"
            : $"{priceText} every {intervalCount} {interval}s";
    }

    private static string PluralizeDay(int days) => days == 1 ? "day" : "days";

    protected sealed class PayPalPlanOption
    {
        public PayPalPlanOption(PayPalPlan plan)
        {
            Plan = plan;
        }

        public PayPalPlan Plan { get; }

        public string Id => Plan.Id;

        public string DisplayText => $"{Plan.Name} — {FormatPayPalPlanTerms(Plan)} — {Plan.Id}";
    }

    protected void CancelChanges()
    {
        _streamPayRateDisplay = _originalStreamPayRateDisplay;
        _streamQualifyingSeconds = _originalStreamQualifyingSeconds;
        _maxAudioUploadSizeMB = _originalMaxAudioUploadSizeMB;
        _maxImageUploadSizeMB = _originalMaxImageUploadSizeMB;
        _reducedStreamQualifyingEnabled = _originalReducedStreamQualifyingEnabled;
        _directToStorageUploadEnabled = _originalDirectToStorageUploadEnabled;
        _pushNotificationsEnabled = _originalPushNotificationsEnabled;
        _lyricsConfidenceThresholdPercent = _originalLyricsConfidenceThresholdPercent;
        _lyricsCompletionEmailsEnabled = _originalLyricsCompletionEmailsEnabled;
        _appVersion = _originalAppVersion;
        _validationErrors.Clear();
        _successMessage = null;
        StateHasChanged();
    }

    protected async Task SaveSettings()
    {
        _validationErrors.Clear();
        _successMessage = null;
        _isSaving = true;

        try
        {
            // Validation
            if (!_streamPayRateDisplay.HasValue || _streamPayRateDisplay.Value <= 0)
            {
                _validationErrors.Add("Stream pay rate must be greater than 0.");
            }

            if (_streamPayRateDisplay.HasValue && _streamPayRateDisplay.Value > 100)
            {
                _validationErrors.Add("Stream pay rate cannot exceed $100 per 1000 streams.");
            }

            if (_streamQualifyingSeconds < 1)
            {
                _validationErrors.Add("Stream qualifying seconds must be at least 1.");
            }

            if (_streamQualifyingSeconds > 300)
            {
                _validationErrors.Add("Stream qualifying seconds cannot exceed 300.");
            }

            if (_maxAudioUploadSizeMB < 1)
            {
                _validationErrors.Add("Max audio upload size must be at least 1 MB.");
            }

            if (_maxAudioUploadSizeMB > 500)
            {
                _validationErrors.Add("Max audio upload size cannot exceed 500 MB.");
            }

            if (_maxImageUploadSizeMB < 1)
            {
                _validationErrors.Add("Max image upload size must be at least 1 MB.");
            }

            if (_maxImageUploadSizeMB > 100)
            {
                _validationErrors.Add("Max image upload size cannot exceed 100 MB.");
            }

            // Validated here as well as bounded on the control, because an out-of-range value does
            // not fail on the way in - SetLyricsConfidenceThresholdAsync clamps it, and
            // GetLyricsConfidenceThresholdAsync silently returns the 0.7 default for anything outside
            // 0-1. Between them a mistyped threshold would look like a save that did not take.
            if (_lyricsConfidenceThresholdPercent < 0m || _lyricsConfidenceThresholdPercent > 100m)
            {
                _validationErrors.Add("Lyric timing confidence must be between 0 and 100%.");
            }

            _appVersion = _appVersion.Trim();

            if (string.IsNullOrWhiteSpace(_appVersion))
            {
                _validationErrors.Add("App version cannot be empty.");
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Save the stream pay rate (convert from per-1000-streams to per-stream)
            await AppSettingsService.SetStreamPayRateAsync(_streamPayRateDisplay!.Value / 1000);

            // Save the stream qualifying seconds
            await AppSettingsService.SetStreamQualifyingSecondsAsync(_streamQualifyingSeconds);

            // Save the max audio upload size
            await AppSettingsService.SetMaxAudioUploadSizeMBAsync(_maxAudioUploadSizeMB);

            // Save the max image upload size
            await AppSettingsService.SetMaxImageUploadSizeMBAsync(_maxImageUploadSizeMB);

            // Written only on a change, and logged loudly either way. This one is a rollout switch
            // rather than a tuning value: knowing when it was flipped is what makes a later "uploads
            // started failing at some point yesterday" answerable.
            if (_pushNotificationsEnabled != _originalPushNotificationsEnabled)
            {
                await AppSettingsService.SetPushNotificationsEnabledAsync(_pushNotificationsEnabled);

                Logger.LogWarning(
                    "Push notifications switched {State} by an admin.",
                    _pushNotificationsEnabled ? "ON" : "OFF");
            }

            if (_directToStorageUploadEnabled != _originalDirectToStorageUploadEnabled)
            {
                await AppSettingsService.SetDirectToStorageUploadEnabledAsync(_directToStorageUploadEnabled);

                Logger.LogWarning(
                    "Direct-to-storage creator uploads turned {State} by an administrator.",
                    _directToStorageUploadEnabled ? "ON" : "OFF");
            }

            // Logged loudly for the same reason as the switch above, and then some: this changes how
            // many streams every creator is credited with, so a later question about a step in the
            // payout figures needs a timestamped answer.
            if (_reducedStreamQualifyingEnabled != _originalReducedStreamQualifyingEnabled)
            {
                await AppSettingsService.SetReducedStreamQualifyingEnabledAsync(_reducedStreamQualifyingEnabled);

                Logger.LogWarning(
                    "Reduced stream-qualifying threshold turned {State} by an administrator. Effective "
                    + "threshold is now each creator's contracted seconds {Adjustment}.",
                    _reducedStreamQualifyingEnabled ? "ON" : "OFF",
                    _reducedStreamQualifyingEnabled
                        ? $"minus {StreamQualifyingPolicy.PromotionalReductionSeconds}, floored at {StreamQualifyingPolicy.MinimumQualifyingSeconds}"
                        : "as agreed");
            }

            // Written only on a change, and logged, because this one silently re-words what every
            // creator is told about work that has already finished - the banner reads the threshold
            // live rather than storing a verdict with the song. It does not re-grade anything: no
            // alignment has ever published itself, so moving this cannot change what a listener
            // sees, only whether a creator is greeted with "have a listen" or "expect to do some
            // tapping".
            if (_lyricsConfidenceThresholdPercent != _originalLyricsConfidenceThresholdPercent)
            {
                await AppSettingsService.SetLyricsConfidenceThresholdAsync(
                    (double)(_lyricsConfidenceThresholdPercent / 100m));

                Logger.LogWarning(
                    "Lyric timing confidence threshold changed from {Old}% to {New}% by an administrator.",
                    _originalLyricsConfidenceThresholdPercent,
                    _lyricsConfidenceThresholdPercent);
            }

            if (_lyricsCompletionEmailsEnabled != _originalLyricsCompletionEmailsEnabled)
            {
                await AppSettingsService.SetLyricsCompletionEmailsEnabledAsync(_lyricsCompletionEmailsEnabled);

                Logger.LogWarning(
                    "Lyric timing completion emails turned {State} by an administrator.",
                    _lyricsCompletionEmailsEnabled ? "ON" : "OFF");
            }

            // Save the app version (only if changed)
            if (_appVersion != _originalAppVersion)
            {
                await AppSettingsService.SetAppVersionAsync(_appVersion);
            }

            // Update the original values to reflect the saved state
            _originalStreamPayRateDisplay = _streamPayRateDisplay;
            _originalStreamQualifyingSeconds = _streamQualifyingSeconds;
            _originalLyricsConfidenceThresholdPercent = _lyricsConfidenceThresholdPercent;
            _originalLyricsCompletionEmailsEnabled = _lyricsCompletionEmailsEnabled;
            _originalMaxAudioUploadSizeMB = _maxAudioUploadSizeMB;
            _originalMaxImageUploadSizeMB = _maxImageUploadSizeMB;
            _originalDirectToStorageUploadEnabled = _directToStorageUploadEnabled;
            _originalPushNotificationsEnabled = _pushNotificationsEnabled;
            _originalAppVersion = _appVersion;
            _successMessage = $"Settings saved successfully. Stream pay rate: ${_streamPayRateDisplay.Value:F2} per 1000 streams, Stream qualifying seconds: {_streamQualifyingSeconds}, Max audio upload size: {_maxAudioUploadSizeMB} MB, Max image upload size: {_maxImageUploadSizeMB} MB, Browser-direct uploads: {(_directToStorageUploadEnabled ? "on" : "off")}";

            Logger.LogInformation("Settings updated - Stream pay rate: ${StreamRate} per 1000 streams, Stream qualifying seconds: {Seconds}, Max audio upload size: {MaxAudioMB} MB, Max image upload size: {MaxImageMB} MB",
                _streamPayRateDisplay.Value, _streamQualifyingSeconds, _maxAudioUploadSizeMB, _maxImageUploadSizeMB);
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving settings: {ex.Message}");
            Logger.LogError(ex, "Failed to save settings");
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    protected void CancelNotificationChanges()
    {
        _notifyRegistration = _originalNotifyRegistration;
        _notifyEmailConfirmed = _originalNotifyEmailConfirmed;
        _notifyTaxFormCompleted = _originalNotifyTaxFormCompleted;
        _notifyCreatorStatusGained = _originalNotifyCreatorStatusGained;
        _notifyCreatorStatusLost = _originalNotifyCreatorStatusLost;
        _notifyUploadCompleted = _originalNotifyUploadCompleted;
        _notifySongRenamed = _originalNotifySongRenamed;
        _notifySongArtUpdated = _originalNotifySongArtUpdated;
        _notifyLyricsChanged = _originalNotifyLyricsChanged;
        _notifyTipFraudPrevented = _originalNotifyTipFraudPrevented;
        StateHasChanged();
    }

    protected async Task SaveNotificationSettings()
    {
        _isSavingNotifications = true;
        try
        {
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyRegistrationKey, _notifyRegistration);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyEmailConfirmedKey, _notifyEmailConfirmed);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyTaxFormCompletedKey, _notifyTaxFormCompleted);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyCreatorStatusGainedKey, _notifyCreatorStatusGained);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyCreatorStatusLostKey, _notifyCreatorStatusLost);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyUploadCompletedKey, _notifyUploadCompleted);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifySongRenamedKey, _notifySongRenamed);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifySongArtUpdatedKey, _notifySongArtUpdated);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyLyricsChangedKey, _notifyLyricsChanged);
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyTipFraudPreventedKey, _notifyTipFraudPrevented);

            _originalNotifyRegistration = _notifyRegistration;
            _originalNotifyEmailConfirmed = _notifyEmailConfirmed;
            _originalNotifyTaxFormCompleted = _notifyTaxFormCompleted;
            _originalNotifyCreatorStatusGained = _notifyCreatorStatusGained;
            _originalNotifyCreatorStatusLost = _notifyCreatorStatusLost;
            _originalNotifyUploadCompleted = _notifyUploadCompleted;
            _originalNotifySongRenamed = _notifySongRenamed;
            _originalNotifySongArtUpdated = _notifySongArtUpdated;
            _originalNotifyLyricsChanged = _notifyLyricsChanged;
            _originalNotifyTipFraudPrevented = _notifyTipFraudPrevented;

            _successMessage = "Admin notification settings saved successfully.";
            Logger.LogInformation("Admin notification settings updated");
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving notification settings: {ex.Message}");
            Logger.LogError(ex, "Failed to save notification settings");
        }
        finally
        {
            _isSavingNotifications = false;
            StateHasChanged();
        }
    }

    protected void CancelMaintenanceChanges()
    {
        _maintenanceStartEastern = _originalMaintenanceStartEastern;
        _maintenanceEndEastern = _originalMaintenanceEndEastern;
        _maintenanceValidationErrors.Clear();
        _maintenanceSuccessMessage = null;
        StateHasChanged();
    }

    protected async Task SaveMaintenanceSettings()
    {
        _maintenanceValidationErrors.Clear();
        _maintenanceSuccessMessage = null;
        _isSavingMaintenance = true;

        try
        {
            if (!_maintenanceStartEastern.HasValue)
            {
                _maintenanceValidationErrors.Add("Maintenance start date/time is required.");
            }

            if (!_maintenanceEndEastern.HasValue)
            {
                _maintenanceValidationErrors.Add("Maintenance end date/time is required.");
            }

            if (_maintenanceStartEastern.HasValue && _maintenanceEndEastern.HasValue
                && _maintenanceEndEastern.Value <= _maintenanceStartEastern.Value)
            {
                _maintenanceValidationErrors.Add("End date/time must be after start date/time.");
            }

            if (_maintenanceValidationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            var startUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_maintenanceStartEastern!.Value, DateTimeKind.Unspecified), EasternTimeZone);
            await AppSettingsService.SetTaxBanditsMaintenanceStartUtcAsync(startUtc);

            var endUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_maintenanceEndEastern!.Value, DateTimeKind.Unspecified), EasternTimeZone);
            await AppSettingsService.SetTaxBanditsMaintenanceEndUtcAsync(endUtc);

            _originalMaintenanceStartEastern = _maintenanceStartEastern;
            _originalMaintenanceEndEastern = _maintenanceEndEastern;

            _maintenanceSuccessMessage = "Tax Bandits maintenance window settings saved successfully.";
            Logger.LogInformation("Tax Bandits maintenance window settings updated - Start (ET): {Start}, End (ET): {End}",
                _maintenanceStartEastern, _maintenanceEndEastern);
        }
        catch (Exception ex)
        {
            _maintenanceValidationErrors.Add($"Error saving maintenance settings: {ex.Message}");
            Logger.LogError(ex, "Failed to save Tax Bandits maintenance window settings");
        }
        finally
        {
            _isSavingMaintenance = false;
            StateHasChanged();
        }
    }

    protected void CancelSiteMaintenanceChanges()
    {
        _siteMaintenanceStartPacific = _originalSiteMaintenanceStartPacific;
        _siteMaintenanceEndPacific = _originalSiteMaintenanceEndPacific;
        _siteMaintenanceValidationErrors.Clear();
        _siteMaintenanceSuccessMessage = null;
        StateHasChanged();
    }

    protected async Task SaveSiteMaintenanceSettings()
    {
        _siteMaintenanceValidationErrors.Clear();
        _siteMaintenanceSuccessMessage = null;
        _isSavingSiteMaintenance = true;

        try
        {
            if (!_siteMaintenanceStartPacific.HasValue)
            {
                _siteMaintenanceValidationErrors.Add("Start date/time is required.");
            }

            if (!_siteMaintenanceEndPacific.HasValue)
            {
                _siteMaintenanceValidationErrors.Add("End date/time is required.");
            }

            if (_siteMaintenanceStartPacific.HasValue && _siteMaintenanceEndPacific.HasValue
                && _siteMaintenanceEndPacific.Value <= _siteMaintenanceStartPacific.Value)
            {
                _siteMaintenanceValidationErrors.Add("End date/time must be after start date/time.");
            }

            if (_siteMaintenanceValidationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            var startUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_siteMaintenanceStartPacific!.Value, DateTimeKind.Unspecified), PacificTimeZone);
            await AppSettingsService.SetSiteMaintenanceStartUtcAsync(startUtc);

            var endUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_siteMaintenanceEndPacific!.Value, DateTimeKind.Unspecified), PacificTimeZone);
            await AppSettingsService.SetSiteMaintenanceEndUtcAsync(endUtc);

            _originalSiteMaintenanceStartPacific = _siteMaintenanceStartPacific;
            _originalSiteMaintenanceEndPacific = _siteMaintenanceEndPacific;

            _siteMaintenanceSuccessMessage = "Site maintenance window settings saved successfully.";
            Logger.LogInformation("Site maintenance window settings updated - Start (PT): {Start}, End (PT): {End}",
                _siteMaintenanceStartPacific, _siteMaintenanceEndPacific);

            await MaintenanceHubContext.Clients.All.SendAsync(SignalRMethodNames.ReceiveMaintenanceUpdate);
        }
        catch (Exception ex)
        {
            _siteMaintenanceValidationErrors.Add($"Error saving site maintenance settings: {ex.Message}");
            Logger.LogError(ex, "Failed to save site maintenance window settings");
        }
        finally
        {
            _isSavingSiteMaintenance = false;
            StateHasChanged();
        }
    }
}
