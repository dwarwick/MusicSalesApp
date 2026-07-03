using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Notifications;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Pages.Creator;

public partial class CreatorSettingsModel : BlazorBase, IDisposable
{
    protected bool _loading = true;
    protected bool _isAuthenticated = false;
    private bool _hasLoadedData = false;

    protected string _successMessage = string.Empty;
    protected string _errorMessage = string.Empty;
    protected string _userEmail = string.Empty;
    protected string _userTimeZoneId = UserTimeZoneDisplayHelper.UtcTimeZoneId;

    protected bool _isActiveCreator = false;
    protected string _creatorOnboardingStatus = null;
    protected string _creatorTaxFormStatus = null;
    protected string _lastTaxFormErrorMessage = null;
    protected TimeSpan? _tinMatchCooldownRemaining = null;
    private System.Threading.Timer _cooldownTimer;
    protected string _creatorDisplayName = string.Empty;
    protected string _creatorBio = string.Empty;
    protected string _creatorPayPalEmail = string.Empty;
    protected string _paypalEmail = string.Empty;
    protected bool _paypalAccountAffirmed = false;
    protected bool _payoutRequirementsAcknowledged = false;
    protected bool _submitTaxFormNow = false;
    protected bool _startingOnboarding = false;
    protected bool _completingOnboarding = false;
    protected bool _stoppingCreatorStatus = false;
    protected bool _updatingTaxForm = false;
    protected string _stopSellingConfirmEmail = string.Empty;
    protected bool CanConfirmStopSelling =>
        string.Equals(_stopSellingConfirmEmail?.Trim(), _userEmail, StringComparison.Ordinal);
    private int? _creatorId = null;
    private bool _creatorSignupConversionTracked = false;
    private bool _creatorActivatedFunnelTracked = false;
    private bool _creatorSettingsViewedTracked = false;
    private bool _creatorSignupStartedTracked = false;

    protected string _locationCertification = "None";
    protected bool _acknowledgmentAccepted = false;

    protected string _editCreatorDisplayName = string.Empty;
    protected string _editCreatorBio = string.Empty;
    protected bool _savingCreatorProfile = false;
    protected string _creatorProfileMessage = string.Empty;
    protected bool _creatorProfileSuccess = false;

    protected int _creatorStreamQualifyingSeconds = 30;
    protected decimal _creatorStreamPayRateDisplay = 5.00m;

    protected bool _showMaintenanceWarning = false;
    protected string _maintenanceStartLocal = string.Empty;
    protected string _maintenanceEndLocal = string.Empty;
    protected string _maintenanceTimeZoneAbbreviation = string.Empty;

    protected bool CanStartOnboarding => IsOptionalPayPalSetupValid
        && _locationCertification != "None"
        && _acknowledgmentAccepted
        && _payoutRequirementsAcknowledged;

    private bool IsOptionalPayPalSetupValid =>
        (string.IsNullOrWhiteSpace(_creatorPayPalEmail) && !_paypalAccountAffirmed)
        || (!string.IsNullOrWhiteSpace(_creatorPayPalEmail) && _paypalAccountAffirmed);

    protected bool IsPayoutPayPalReady =>
        !string.IsNullOrWhiteSpace(_paypalEmail) && _paypalAccountAffirmed;

    protected bool IsPayoutTaxReady =>
        string.Equals(_creatorTaxFormStatus, TaxFormStatus.Completed.ToString(), StringComparison.Ordinal);

    protected bool IsPayoutEligibilityReady =>
        _acknowledgmentAccepted
        && (_locationCertification == CreatorLocationCertification.USPerson.ToString()
            || _locationCertification == CreatorLocationCertification.NonUSPersonOutsideUS.ToString());

    protected bool IsPayoutReady =>
        IsPayoutPayPalReady
        && IsPayoutTaxReady
        && IsPayoutEligibilityReady
        && _payoutRequirementsAcknowledged;

    protected SfDialog _stopSellingDialog;
    protected SfDialog _creatorActivatedDialog;
    protected SfToast _toastRef;

    private ApplicationUser _currentUser;
    private Action<WebhookStatusMessage> _webhookStatusHandler;
    private bool _wasActiveCreatorBefore;

    [SupplyParameterFromQuery(Name = "creator_onboarding")]
    public string CreatorOnboardingResult { get; set; }

    [SupplyParameterFromQuery(Name = "tracking_id")]
    public string CreatorTrackingId { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    _isAuthenticated = true;
                    _currentUser = await UserManager.GetUserAsync(user);
                    if (_currentUser != null)
                    {
                        _userEmail = _currentUser.Email ?? string.Empty;
                        _userTimeZoneId = UserTimeZoneDisplayHelper.GetTimeZoneId(_currentUser);

                        await DetectAndPersistUserTimeZoneAsync();
                        await LoadCreatorStatus();
                        await TrackCreatorSettingsViewedAsync();

                        _webhookStatusHandler = OnWebhookStatusReceived;
                        WebhookStatusHubClient.OnWebhookStatusReceived += _webhookStatusHandler;
                        await WebhookStatusHubClient.StartAsync();

                        if (!string.IsNullOrEmpty(CreatorOnboardingResult) && CreatorOnboardingResult == "complete")
                        {
                            await CompleteCreatorOnboarding();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading creator settings data");
                _errorMessage = "Error loading creator settings.";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadCreatorStatus()
    {
        try
        {
            _showMaintenanceWarning = await AppSettingsService.ShouldShowTaxBanditsMaintenanceWarningAsync();
            if (_showMaintenanceWarning)
            {
                var startUtc = await AppSettingsService.GetTaxBanditsMaintenanceStartUtcAsync();
                var endUtc = await AppSettingsService.GetTaxBanditsMaintenanceEndUtcAsync();
                try
                {
                    var localInfo = await JS.InvokeAsync<CreatorSettingsMaintenanceInfo>("getMaintenanceLocalTime",
                        startUtc?.ToString("O"), endUtc?.ToString("O"));
                    _maintenanceStartLocal = localInfo.StartLocal;
                    _maintenanceEndLocal = localInfo.EndLocal;
                    _maintenanceTimeZoneAbbreviation = localInfo.TimeZoneAbbreviation;
                }
                catch
                {
                    _maintenanceStartLocal = startUtc?.ToString("g") ?? "";
                    _maintenanceEndLocal = endUtc?.ToString("g") ?? "";
                    _maintenanceTimeZoneAbbreviation = "UTC";
                }
            }

            var creator = await CreatorService.GetCreatorByUserIdAsync(_currentUser.Id);
            if (creator != null)
            {
                _creatorId = creator.Id;
                _isActiveCreator = creator.IsActive;
                _creatorOnboardingStatus = creator.OnboardingStatus.ToString();
                _creatorTaxFormStatus = creator.TaxFormStatus.ToString();
                _lastTaxFormErrorMessage = creator.LastTaxFormErrorMessage;

                if (creator.TaxFormStatus == TaxFormStatus.Failed && creator.LastTinMatchFailedAt.HasValue)
                {
                    var cooldownEnd = creator.LastTinMatchFailedAt.Value.AddHours(24);
                    var remaining = cooldownEnd - DateTime.UtcNow;
                    _tinMatchCooldownRemaining = remaining > TimeSpan.Zero ? remaining : null;

                    if (_tinMatchCooldownRemaining.HasValue)
                    {
                        StartCooldownTimer();
                    }
                }
                else
                {
                    _tinMatchCooldownRemaining = null;
                }

                _creatorDisplayName = creator.DisplayName ?? string.Empty;
                _creatorBio = creator.Bio ?? string.Empty;
                _creatorPayPalEmail = creator.PayPalEmail ?? string.Empty;
                _paypalEmail = creator.PayPalEmail ?? string.Empty;
                _paypalAccountAffirmed = creator.PayPalAccountAffirmed;
                _payoutRequirementsAcknowledged = creator.PayoutRequirementsAcknowledged;
                _locationCertification = creator.LocationCertification.ToString();
                _acknowledgmentAccepted = creator.AcknowledgmentAccepted;
                _creatorStreamQualifyingSeconds = creator.StreamQualifyingSeconds;
                _creatorStreamPayRateDisplay = creator.StreamPayRate * 1000;
                _editCreatorDisplayName = _creatorDisplayName;
                _editCreatorBio = _creatorBio;
            }
            else
            {
                _creatorId = null;
                _isActiveCreator = false;
                _creatorOnboardingStatus = null;
                _creatorTaxFormStatus = null;
                _lastTaxFormErrorMessage = null;
                _creatorPayPalEmail = string.Empty;
                _paypalEmail = string.Empty;
                _paypalAccountAffirmed = false;
                _payoutRequirementsAcknowledged = false;
                _locationCertification = "None";
                _acknowledgmentAccepted = false;
                _submitTaxFormNow = false;

                _creatorStreamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
                var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
                _creatorStreamPayRateDisplay = streamPayRate * 1000;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading creator status");
        }
    }

    protected async Task SaveCreatorProfile()
    {
        _creatorProfileMessage = string.Empty;
        _savingCreatorProfile = true;

        try
        {
            var creator = await CreatorService.GetCreatorByUserIdAsync(_currentUser.Id);
            if (creator == null)
            {
                _creatorProfileMessage = "Creator profile not found.";
                _creatorProfileSuccess = false;
                return;
            }

            if (!string.IsNullOrEmpty(_editCreatorDisplayName) && _editCreatorDisplayName.Length > 20)
            {
                _creatorProfileMessage = "Display name must be 20 characters or less.";
                _creatorProfileSuccess = false;
                return;
            }

            await CreatorService.UpdateCreatorProfileAsync(creator.Id, _editCreatorDisplayName, _editCreatorBio);

            _creatorDisplayName = _editCreatorDisplayName;
            _creatorBio = _editCreatorBio;

            _creatorProfileMessage = "Profile saved successfully!";
            _creatorProfileSuccess = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving creator profile");
            _creatorProfileMessage = $"Error saving profile: {ex.Message}";
            _creatorProfileSuccess = false;
        }
        finally
        {
            _savingCreatorProfile = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task SavePayPalEmail()
    {
        _errorMessage = string.Empty;
        _successMessage = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(_paypalEmail))
            {
                _errorMessage = "Please enter a payout email address.";
                return;
            }

            if (!_paypalAccountAffirmed)
            {
                _errorMessage = "Please confirm that this PayPal account is valid and can receive payouts.";
                return;
            }

            if (!_paypalEmail.Contains("@") || !_paypalEmail.Contains("."))
            {
                _errorMessage = "Please enter a valid email address.";
                return;
            }

            var creator = await CreatorService.UpdateCreatorPayoutEmailAsync(_currentUser.Id, _paypalEmail, _paypalAccountAffirmed);
            if (creator == null)
            {
                _errorMessage = "Creator record not found.";
                return;
            }

            _successMessage = "Payout email updated successfully!";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving payout email");
            _errorMessage = "Failed to save payout email. Please try again.";
        }
    }

    protected async Task StartCreatorOnboarding()
    {
        _startingOnboarding = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        await InvokeAsync(StateHasChanged);

        try
        {
            if (!Enum.TryParse<CreatorLocationCertification>(_locationCertification, out var locationCertEnum))
            {
                locationCertEnum = CreatorLocationCertification.None;
            }

            var result = await CreatorService.StartOnboardingAsync(new CreatorOnboardingInput
            {
                UserId = _currentUser.Id,
                UserEmail = _currentUser.Email,
                DisplayName = _creatorDisplayName,
                Bio = _creatorBio,
                PayPalEmail = _creatorPayPalEmail,
                PayPalAccountAffirmed = _paypalAccountAffirmed,
                LocationCertification = locationCertEnum,
                AcknowledgmentAccepted = _acknowledgmentAccepted,
                PayoutRequirementsAcknowledged = _payoutRequirementsAcknowledged,
                SubmitTaxFormNow = _submitTaxFormNow
            });

            if (!result.Success)
            {
                _errorMessage = result.ErrorMessage ?? "Failed to start creator onboarding. Please try again.";
            }
            else if (result.IsIneligible)
            {
                _errorMessage = "At this time, Streamtunes does not support paid creator participation for non-U.S. persons who will perform any creator activities while physically present in the United States. You are not eligible to register as a creator at this time.";
                await LoadCreatorStatus();
            }
            else if (result.TaxFormPending)
            {
                await TrackCreatorSignupStartedAsync(FunnelAnalyticsLabels.CreatorSignupTaxFormPending);
                NavigationManager.NavigateTo(AppPageRoutes.SubmitTaxForm);
                return;
            }
            else if (result.IsActive)
            {
                await TrackCreatorSignupStartedAsync(FunnelAnalyticsLabels.CreatorSignupActive);
                await LoadCreatorStatus();
                await ShowCreatorActivatedDialog();
            }
            else
            {
                await TrackCreatorSignupStartedAsync(FunnelAnalyticsLabels.CreatorSignupStarted);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error starting creator onboarding");
            _errorMessage = "Error starting creator onboarding. Please refresh the page to see your current status.";
            await LoadCreatorStatus();
        }
        finally
        {
            _startingOnboarding = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task CompleteCreatorOnboarding()
    {
        _completingOnboarding = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await CreatorService.CompleteOnboardingAsync(_currentUser.Id);

            if (!result.Success)
            {
                _errorMessage = result.ErrorMessage ?? "Could not verify your creator setup. Please try again.";
            }
            else if (result.IsActive)
            {
                _isActiveCreator = true;
                _creatorOnboardingStatus = "Completed";
                await ShowCreatorActivatedDialog();
            }
            else if (result.PaymentsReceivable || result.PrimaryEmailConfirmed)
            {
                _successMessage = "Your creator setup is saved. Complete any remaining payout tasks before your next payout.";
                _creatorOnboardingStatus = "InProgress";
            }
            else
            {
                _errorMessage = "Creator activation is not complete. Please review the required creator certifications.";
            }

            await LoadCreatorStatus();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error completing creator onboarding");
            _errorMessage = $"Error completing creator onboarding: {ex.Message}";
        }
        finally
        {
            _completingOnboarding = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task ShowCreatorActivatedDialog()
    {
        await TrackCreatorActivatedFunnelAsync();
        await TrackCreatorSignupConversionAsync();

        if (_creatorActivatedDialog != null)
        {
            await _creatorActivatedDialog.ShowAsync();
        }
    }

    protected void LogoutAfterCreatorActivation()
    {
        NavigationManager.NavigateTo("/account/logout", forceLoad: true);
    }

    protected void NavigateToTaxForm()
    {
        NavigationManager.NavigateTo("/submittaxform");
    }

    protected async Task InitiateTaxFormUpdate()
    {
        _updatingTaxForm = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await CreatorService.InitiateTaxFormUpdateAsync(_currentUser.Id, _currentUser.Email);

            if (result.Success)
            {
                NavigationManager.NavigateTo("/submittaxform");
                return;
            }

            _errorMessage = result.ErrorMessage ?? "Failed to initiate tax form update.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initiating tax form update");
            _errorMessage = "An error occurred while initiating the tax form update. Please try again.";
        }
        finally
        {
            _updatingTaxForm = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void NavigateToUpload()
    {
        NavigationManager.NavigateTo("/upload-files");
    }

    protected void NavigateToManageSongs()
    {
        NavigationManager.NavigateTo("/creator/songs");
    }

    protected async Task ShowStopSellingConfirmation()
    {
        _stopSellingConfirmEmail = string.Empty;
        await _stopSellingDialog.ShowAsync();
    }

    protected async Task CloseStopSellingDialog()
    {
        await _stopSellingDialog.HideAsync();
    }

    protected async Task ConfirmStopSelling()
    {
        if (!CanConfirmStopSelling)
        {
            _errorMessage = "Please enter your email address to confirm.";
            return;
        }

        _stoppingCreatorStatus = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        await InvokeAsync(StateHasChanged);

        try
        {
            var success = await CreatorService.StopBeingCreatorAsync(_currentUser.Id);
            if (success)
            {
                _successMessage = "You are no longer a creator. All your music has been removed from the platform.";
                _isActiveCreator = false;
                _creatorOnboardingStatus = "Suspended";
                await _stopSellingDialog.HideAsync();
            }
            else
            {
                _errorMessage = "You are not currently a creator or there was an error processing your request.";
            }

            await LoadCreatorStatus();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error stopping creator status");
            _errorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _stoppingCreatorStatus = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task TrackCreatorSignupConversionAsync()
    {
        if (_creatorSignupConversionTracked || !_isActiveCreator)
        {
            return;
        }

        if (!IsGoogleAdsTrackingAllowedForCurrentHost())
        {
            return;
        }

        var googleAdsTagId = Configuration[GoogleAdsTrackingConfigKeys.TagId];
        var creatorSignupConversionLabel = Configuration[GoogleAdsTrackingConfigKeys.CreatorSignupConversionLabel];
        if (string.IsNullOrWhiteSpace(googleAdsTagId) || string.IsNullOrWhiteSpace(creatorSignupConversionLabel))
        {
            return;
        }

        if (!_creatorId.HasValue && _currentUser != null)
        {
            var creator = await CreatorService.GetCreatorByUserIdAsync(_currentUser.Id);
            if (creator?.IsActive == true)
            {
                _creatorId = creator.Id;
            }
        }

        if (!_creatorId.HasValue)
        {
            return;
        }

        var sendTo = $"{googleAdsTagId}/{creatorSignupConversionLabel}";
        var transactionId = $"{GoogleAdsTrackingConfigKeys.CreatorSignupTransactionIdPrefix}{_creatorId.Value}";
        _creatorSignupConversionTracked = true;

        try
        {
            await JS.InvokeVoidAsync(GoogleAdsTrackingConfigKeys.TrackConversionFunctionName, sendTo, transactionId);
        }
        catch (Exception ex) when (ex is JSException || ex is InvalidOperationException)
        {
            Logger.LogWarning(ex, "Failed to send Google Ads creator signup conversion for creator {CreatorId}", _creatorId.Value);
        }
    }

    private async Task TrackCreatorSettingsViewedAsync()
    {
        if (_creatorSettingsViewedTracked || _currentUser == null)
        {
            return;
        }

        _creatorSettingsViewedTracked = true;

        await TrackCreatorFunnelEventAsync(
            FunnelAnalyticsEvents.CreatorSettingsViewed,
            FunnelAnalyticsLabels.CreatorSettings,
            new Dictionary<string, object>
            {
                [FunnelAnalyticsParameters.CreatorStatus] = _isActiveCreator
                    ? FunnelAnalyticsLabels.CreatorSignupActive
                    : _creatorOnboardingStatus ?? FunnelAnalyticsLabels.CreatorSignupStarted
            });

        await RecordCreatorHistoryAsync(
            UserHistoryEventTypes.CreatorSettingsViewed,
            "Creator / Artist Settings viewed.");
    }

    private async Task TrackCreatorSignupStartedAsync(string creatorStatus)
    {
        if (_creatorSignupStartedTracked || _currentUser == null)
        {
            return;
        }

        _creatorSignupStartedTracked = true;

        await TrackCreatorFunnelEventAsync(
            FunnelAnalyticsEvents.CreatorSignupStarted,
            creatorStatus,
            new Dictionary<string, object>
            {
                [FunnelAnalyticsParameters.CreatorStatus] = creatorStatus
            });

        await RecordCreatorHistoryAsync(
            UserHistoryEventTypes.CreatorSignupStarted,
            $"Creator signup started. Status: {creatorStatus}.");
    }

    private async Task TrackCreatorActivatedFunnelAsync()
    {
        if (_creatorActivatedFunnelTracked || !_isActiveCreator || _currentUser == null)
        {
            return;
        }

        _creatorActivatedFunnelTracked = true;

        await TrackCreatorFunnelEventAsync(
            FunnelAnalyticsEvents.CreatorActivated,
            FunnelAnalyticsLabels.CreatorActivated,
            new Dictionary<string, object>
            {
                [FunnelAnalyticsParameters.CreatorStatus] = FunnelAnalyticsLabels.CreatorSignupActive
            });

        await RecordCreatorHistoryAsync(
            UserHistoryEventTypes.CreatorActivated,
            "Creator account activated.");
    }

    private async Task TrackCreatorFunnelEventAsync(
        string eventName,
        string label,
        Dictionary<string, object> parameters = null)
    {
        var payload = new Dictionary<string, object>
        {
            [FunnelAnalyticsParameters.Category] = FunnelAnalyticsLabels.CreatorCategory,
            [FunnelAnalyticsParameters.Label] = label
        };

        if (parameters != null)
        {
            foreach (var parameter in parameters)
            {
                payload[parameter.Key] = parameter.Value;
            }
        }

        try
        {
            await JS.InvokeVoidAsync(GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName, eventName, payload);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send creator funnel analytics event {EventName}", eventName);
        }
    }

    private async Task RecordCreatorHistoryAsync(string eventType, string description)
    {
        try
        {
            await AdminNotificationService.RecordUserHistoryAsync(
                _currentUser.Id,
                _currentUser.Email ?? _userEmail,
                eventType,
                description);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to record creator funnel history event {EventType}", eventType);
        }
    }

    private bool IsGoogleAdsTrackingAllowedForCurrentHost()
    {
        if (!Configuration.GetValue<bool>(GoogleAdsTrackingConfigKeys.Enabled))
        {
            return false;
        }

        var enabledHosts = Configuration
            .GetSection(GoogleAdsTrackingConfigKeys.EnabledHosts)
            .Get<string[]>() ?? Array.Empty<string>();
        if (enabledHosts.Length == 0)
        {
            return false;
        }

        var currentHost = HttpContextAccessor.HttpContext?.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(currentHost)
            && Uri.TryCreate(NavigationManager.Uri, UriKind.Absolute, out var navigationUri))
        {
            currentHost = navigationUri.Host;
        }

        return !string.IsNullOrWhiteSpace(currentHost)
            && enabledHosts.Contains(currentHost, StringComparer.OrdinalIgnoreCase);
    }

    private async Task DetectAndPersistUserTimeZoneAsync()
    {
        try
        {
            var ianaTimeZone = await JS.InvokeAsync<string>("dashboardHelper.getUserTimeZone");

            if (string.IsNullOrWhiteSpace(ianaTimeZone))
            {
                return;
            }

            UserTimeZoneDisplayHelper.ResolveTimeZone(ianaTimeZone);
            _userTimeZoneId = ianaTimeZone;

            if (_currentUser != null && !string.Equals(_currentUser.TimeZoneId, ianaTimeZone, StringComparison.Ordinal))
            {
                _currentUser.TimeZoneId = ianaTimeZone;
                await UserManager.UpdateAsync(_currentUser);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to detect user timezone for creator settings; using stored or UTC fallback");
        }
    }

    private void StartCooldownTimer()
    {
        _cooldownTimer?.Dispose();
        _cooldownTimer = new System.Threading.Timer(async _ =>
        {
            if (_tinMatchCooldownRemaining.HasValue)
            {
                _tinMatchCooldownRemaining = _tinMatchCooldownRemaining.Value - TimeSpan.FromSeconds(1);
                if (_tinMatchCooldownRemaining.Value <= TimeSpan.Zero)
                {
                    _tinMatchCooldownRemaining = null;
                    _cooldownTimer?.Dispose();
                    _cooldownTimer = null;
                }
                await InvokeAsync(StateHasChanged);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnWebhookStatusReceived(WebhookStatusMessage message)
    {
        if (_currentUser == null || message.UserId != _currentUser.Id)
        {
            return;
        }

        InvokeAsync(async () =>
        {
            try
            {
                var toastModel = new ToastModel
                {
                    Title = message.IsSuccess ? "Success" : "Status Update",
                    Content = message.Message,
                    CssClass = message.IsSuccess ? "e-toast-success" : "e-toast-warning",
                    Icon = message.IsSuccess ? "e-success" : "e-warning"
                };

                if (_toastRef != null)
                {
                    await _toastRef.ShowAsync(toastModel);
                }

                if (message.WebhookType.Contains("PayPal", StringComparison.OrdinalIgnoreCase) ||
                    message.WebhookType.Contains("Creator", StringComparison.OrdinalIgnoreCase) ||
                    message.WebhookType.Contains("TaxForm", StringComparison.OrdinalIgnoreCase))
                {
                    _wasActiveCreatorBefore = _isActiveCreator;
                    await LoadCreatorStatus();

                    if (_isActiveCreator && !_wasActiveCreatorBefore)
                    {
                        await ShowCreatorActivatedDialog();
                    }
                }

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling webhook status update");
            }
        });
    }

    public void Dispose()
    {
        if (_webhookStatusHandler != null)
        {
            WebhookStatusHubClient.OnWebhookStatusReceived -= _webhookStatusHandler;
        }
        _cooldownTimer?.Dispose();
    }
}

public class CreatorSettingsMaintenanceInfo
{
    public string StartLocal { get; set; } = string.Empty;
    public string EndLocal { get; set; } = string.Empty;
    public string TimeZoneAbbreviation { get; set; } = string.Empty;
}
