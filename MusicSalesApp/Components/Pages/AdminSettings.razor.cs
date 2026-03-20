using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Hubs;
using MusicSalesApp.Services;

#nullable enable

namespace MusicSalesApp.Components.Pages;

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
    protected decimal? _subscriptionPrice = null;
    protected decimal? _originalSubscriptionPrice = null;
    protected decimal? _streamPayRateDisplay = null; // Display as per 1000 streams (e.g., 5.00)
    protected decimal? _originalStreamPayRateDisplay = null;
    protected int _streamQualifyingSeconds = 30;
    protected int _originalStreamQualifyingSeconds = 30;
    protected int _maxAudioUploadSizeMB = 100;
    protected int _originalMaxAudioUploadSizeMB = 100;
    protected int _maxImageUploadSizeMB = 20;
    protected int _originalMaxImageUploadSizeMB = 20;
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

    protected bool _hasChanges => _subscriptionPrice != _originalSubscriptionPrice 
                                   || _streamPayRateDisplay != _originalStreamPayRateDisplay
                                   || _streamQualifyingSeconds != _originalStreamQualifyingSeconds
                                   || _maxAudioUploadSizeMB != _originalMaxAudioUploadSizeMB
                                   || _maxImageUploadSizeMB != _originalMaxImageUploadSizeMB
                                   || _appVersion != _originalAppVersion;

    protected bool _hasNotificationChanges => _notifyRegistration != _originalNotifyRegistration
                                             || _notifyEmailConfirmed != _originalNotifyEmailConfirmed
                                             || _notifyTaxFormCompleted != _originalNotifyTaxFormCompleted
                                             || _notifyCreatorStatusGained != _originalNotifyCreatorStatusGained
                                             || _notifyCreatorStatusLost != _originalNotifyCreatorStatusLost
                                             || _notifyUploadCompleted != _originalNotifyUploadCompleted
                                             || _notifySongRenamed != _originalNotifySongRenamed
                                             || _notifySongArtUpdated != _originalNotifySongArtUpdated
                                             || _notifyTipFraudPrevented != _originalNotifyTipFraudPrevented;

    protected bool _hasMaintenanceChanges => _maintenanceStartEastern != _originalMaintenanceStartEastern
                                              || _maintenanceEndEastern != _originalMaintenanceEndEastern;

    protected bool _hasSiteMaintenanceChanges => _siteMaintenanceStartPacific != _originalSiteMaintenanceStartPacific
                                                  || _siteMaintenanceEndPacific != _originalSiteMaintenanceEndPacific;

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
        _subscriptionPrice = await AppSettingsService.GetSubscriptionPriceAsync();
        _originalSubscriptionPrice = _subscriptionPrice;
        
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

        _appVersion = await AppSettingsService.GetAppVersionAsync() ?? string.Empty;
        _originalAppVersion = _appVersion;

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

    protected void CancelChanges()
    {
        _subscriptionPrice = _originalSubscriptionPrice;
        _streamPayRateDisplay = _originalStreamPayRateDisplay;
        _streamQualifyingSeconds = _originalStreamQualifyingSeconds;
        _maxAudioUploadSizeMB = _originalMaxAudioUploadSizeMB;
        _maxImageUploadSizeMB = _originalMaxImageUploadSizeMB;
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
            if (!_subscriptionPrice.HasValue || _subscriptionPrice.Value <= 0)
            {
                _validationErrors.Add("Subscription price must be greater than 0.");
            }

            if (_subscriptionPrice.HasValue && _subscriptionPrice.Value > 999.99m)
            {
                _validationErrors.Add("Subscription price cannot exceed $999.99.");
            }

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

            // Save the subscription price
            await AppSettingsService.SetSubscriptionPriceAsync(_subscriptionPrice!.Value);

            // Save the stream pay rate (convert from per-1000-streams to per-stream)
            await AppSettingsService.SetStreamPayRateAsync(_streamPayRateDisplay!.Value / 1000);

            // Save the stream qualifying seconds
            await AppSettingsService.SetStreamQualifyingSecondsAsync(_streamQualifyingSeconds);

            // Save the max audio upload size
            await AppSettingsService.SetMaxAudioUploadSizeMBAsync(_maxAudioUploadSizeMB);

            // Save the max image upload size
            await AppSettingsService.SetMaxImageUploadSizeMBAsync(_maxImageUploadSizeMB);

            // Save the app version (only if changed)
            if (_appVersion != _originalAppVersion)
            {
                await AppSettingsService.SetAppVersionAsync(_appVersion);
            }

            // Update the original values to reflect the saved state
            _originalSubscriptionPrice = _subscriptionPrice;
            _originalStreamPayRateDisplay = _streamPayRateDisplay;
            _originalStreamQualifyingSeconds = _streamQualifyingSeconds;
            _originalMaxAudioUploadSizeMB = _maxAudioUploadSizeMB;
            _originalMaxImageUploadSizeMB = _maxImageUploadSizeMB;
            _originalAppVersion = _appVersion;
            _successMessage = $"Settings saved successfully. Subscription price: ${_subscriptionPrice.Value:F2}, Stream pay rate: ${_streamPayRateDisplay.Value:F2} per 1000 streams, Stream qualifying seconds: {_streamQualifyingSeconds}, Max audio upload size: {_maxAudioUploadSizeMB} MB, Max image upload size: {_maxImageUploadSizeMB} MB";

            Logger.LogInformation("Settings updated - Subscription price: ${Price}, Stream pay rate: ${StreamRate} per 1000 streams, Stream qualifying seconds: {Seconds}, Max audio upload size: {MaxAudioMB} MB, Max image upload size: {MaxImageMB} MB", 
                _subscriptionPrice.Value, _streamPayRateDisplay.Value, _streamQualifyingSeconds, _maxAudioUploadSizeMB, _maxImageUploadSizeMB);
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
            await AdminNotificationService.SetNotificationEnabledAsync(Services.AdminNotificationService.NotifyTipFraudPreventedKey, _notifyTipFraudPrevented);

            _originalNotifyRegistration = _notifyRegistration;
            _originalNotifyEmailConfirmed = _notifyEmailConfirmed;
            _originalNotifyTaxFormCompleted = _notifyTaxFormCompleted;
            _originalNotifyCreatorStatusGained = _notifyCreatorStatusGained;
            _originalNotifyCreatorStatusLost = _notifyCreatorStatusLost;
            _originalNotifyUploadCompleted = _notifyUploadCompleted;
            _originalNotifySongRenamed = _notifySongRenamed;
            _originalNotifySongArtUpdated = _notifySongArtUpdated;
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
