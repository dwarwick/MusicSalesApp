using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Notifications;
using Syncfusion.Blazor.Popups;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages.Auth;

public partial class ManageAccountModel : BlazorBase, IDisposable
{
    protected bool _loading = true;
    protected bool _isAuthenticated = false;
    private bool _hasLoadedData = false;
    
    protected string _successMessage = string.Empty;
    protected string _errorMessage = string.Empty;
    
    // User email for display
    protected string _userEmail = string.Empty;
    
    // Password change fields
    protected string _currentPassword = string.Empty;
    protected string _newPassword = string.Empty;
    protected string _confirmPassword = string.Empty;

    // Email preferences
    protected bool _receiveNewSongEmails = false;
    
    // Passkey fields
    protected List<Passkey> _passkeys = new();
    protected string _newPasskeyName = string.Empty;
    protected string _renamePasskeyName = string.Empty;
    protected Passkey _selectedPasskey;
    
    // Subscription fields
    protected bool _hasSubscription;
    protected string _subscriptionStatus;
    protected decimal _monthlyPrice;
    protected DateTime? _startDate;
    protected DateTime? _endDate;
    protected DateTime? _nextBillingDate;
    protected string _paypalSubscriptionId;
    protected string _subscriptionPrice = "3.99";
    protected bool _agreeToTerms = false;
    protected bool _subscribing = false;
    protected bool _cancelling = false;
    
    // Account closure
    protected bool _hasPurchasedMusic = false;
    protected string _accountActionConfirmEmail = string.Empty;

    // Creator fields
    protected bool _isActiveCreator = false;
    protected string _creatorOnboardingStatus = null;
    protected string _creatorTaxFormStatus = null;
    protected TimeSpan? _tinMatchCooldownRemaining = null;
    private System.Threading.Timer _cooldownTimer;
    protected string _creatorReferralUrl = null;
    protected string _creatorDisplayName = string.Empty;
    protected string _creatorBio = string.Empty;
    protected string _creatorPayPalEmail = string.Empty;
    protected string _paypalEmail = string.Empty;
    protected bool _paypalAccountAffirmed = false;
    protected bool _startingOnboarding = false;
    protected bool _completingOnboarding = false;
    protected bool _stoppingCreatorStatus = false;
    protected bool _updatingTaxForm = false;
    protected string _stopSellingConfirmEmail = string.Empty;
    
    // Creator attestation fields
    protected string _locationCertification = "None";
    protected bool _acknowledgmentAccepted = false;
    
    // Creator profile editing
    protected string _editCreatorDisplayName = string.Empty;
    protected string _editCreatorBio = string.Empty;
    protected bool _savingCreatorProfile = false;
    protected string _creatorProfileMessage = string.Empty;
    protected bool _creatorProfileSuccess = false;

    // Creator stream definition display values
    protected int _creatorStreamQualifyingSeconds = 30;
    protected decimal _creatorStreamPayRateDisplay = 5.00m;

    // Tax Bandits maintenance window
    protected bool _showMaintenanceWarning = false;
    protected string _maintenanceStartLocal = string.Empty;
    protected string _maintenanceEndLocal = string.Empty;
    protected string _maintenanceTimeZoneAbbreviation = string.Empty;
    
    /// <summary>
    /// Returns true if the user can start the creator onboarding process.
    /// </summary>
    protected bool CanStartOnboarding => !string.IsNullOrWhiteSpace(_creatorPayPalEmail) 
        && _paypalAccountAffirmed 
        && _locationCertification != "None"
        && _acknowledgmentAccepted;
    
    // Dialogs
    protected SfDialog _addPasskeyDialog;
    protected SfDialog _renamePasskeyDialog;
    protected SfDialog _deletePasskeyDialog;
    protected SfDialog _accountClosureDialog;
    protected SfDialog _suspendAccountDialog;
    protected SfDialog _deleteAccountDialog;
    protected SfDialog _stopSellingDialog;
    
    // Toast for webhook status notifications
    protected SfToast _toastRef;
    
    private ApplicationUser _currentUser;
    private Action<WebhookStatusMessage> _webhookStatusHandler;

    [Inject]
    private IDbContextFactory<AppDbContext> DbContextFactory { get; set; }

    [SupplyParameterFromQuery(Name = "success")]
    public bool? Success { get; set; }

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
                        // Load email preferences
                        _receiveNewSongEmails = _currentUser.ReceiveNewSongEmails;
                        
                        await LoadPasskeys();
                        await CheckPurchasedMusic();
                        await LoadSubscriptionStatus();
                        await LoadCreatorStatus();

                        // Subscribe to SignalR webhook status updates
                        _webhookStatusHandler = OnWebhookStatusReceived;
                        WebhookStatusHubClient.OnWebhookStatusReceived += _webhookStatusHandler;
                        await WebhookStatusHubClient.StartAsync();

                        // Handle return from PayPal creator onboarding
                        if (!string.IsNullOrEmpty(CreatorOnboardingResult) && CreatorOnboardingResult == "complete")
                        {
                            await CompleteCreatorOnboarding();
                        }
                        
                        // Handle return from PayPal subscription
                        if (Success.HasValue)
                        {
                            if (Success.Value)
                            {
                                try
                                {
                                    var activateResponse = await Http.PostAsync("api/subscription/activate-current", null);
                                    if (activateResponse.IsSuccessStatusCode)
                                    {
                                        _successMessage = "Your subscription has been activated successfully!";
                                        await LoadSubscriptionStatus();
                                    }
                                    else
                                    {
                                        _errorMessage = "Failed to activate subscription. Please contact support.";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Error activating subscription");
                                    _errorMessage = "An error occurred while activating your subscription.";
                                }
                            }
                            else
                            {
                                try
                                {
                                    var deleteResponse = await Http.PostAsync("api/subscription/delete-pending", null);
                                    if (deleteResponse.IsSuccessStatusCode)
                                    {
                                        _errorMessage = "Subscription setup was cancelled.";
                                    }
                                    else
                                    {
                                        _errorMessage = "Subscription setup was cancelled. Please try again if you wish to subscribe.";
                                    }
                                    await LoadSubscriptionStatus();
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Error deleting pending subscription");
                                    _errorMessage = "Subscription setup was cancelled.";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading account data");
                _errorMessage = "Error loading account data.";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadPasskeys()
    {
        try
        {
            _passkeys = await PasskeyService.GetUserPasskeysAsync(_currentUser.Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading passkeys");
        }
    }

    protected async Task CheckPurchasedMusic()
    {
        // No longer checking for purchased music since individual purchases are removed
        // Users now access music through subscriptions only
        _hasPurchasedMusic = false;
    }

    protected async Task ChangePassword()
    {
        _successMessage = string.Empty;
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_currentPassword) || 
            string.IsNullOrWhiteSpace(_newPassword) || 
            string.IsNullOrWhiteSpace(_confirmPassword))
        {
            _errorMessage = "All password fields are required.";
            return;
        }

        if (_newPassword != _confirmPassword)
        {
            _errorMessage = "New password and confirmation do not match.";
            return;
        }

        try
        {
            var result = await UserManager.ChangePasswordAsync(_currentUser, _currentPassword, _newPassword);
            
            if (result.Succeeded)
            {
                _successMessage = "Password changed successfully.";
                _currentPassword = string.Empty;
                _newPassword = string.Empty;
                _confirmPassword = string.Empty;
                
                // Send password changed email notification (only if email is available)
                var userEmail = _currentUser.Email;
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        var baseUrl = NavigationManager.BaseUri;
                        var userName = _currentUser.UserName ?? userEmail;
                        await AccountEmailService.SendPasswordChangedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send password changed email to user {UserId}", _currentUser.Id);
                        // Don't fail the password change if email sending fails
                    }
                }
            }
            else
            {
                _errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error changing password");
            _errorMessage = "An error occurred while changing your password.";
        }
    }

    protected async Task SaveEmailPreferences()
    {
        _successMessage = string.Empty;
        _errorMessage = string.Empty;

        try
        {
            _currentUser.ReceiveNewSongEmails = _receiveNewSongEmails;
            var result = await UserManager.UpdateAsync(_currentUser);
            
            if (result.Succeeded)
            {
                _successMessage = "Email preferences saved successfully.";
            }
            else
            {
                _errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving email preferences");
            _errorMessage = "An error occurred while saving your email preferences.";
        }
    }

    protected async Task ShowAddPasskeyDialog()
    {
        _newPasskeyName = string.Empty;
        await _addPasskeyDialog.ShowAsync();
    }

    protected async Task CloseAddPasskeyDialog()
    {
        await _addPasskeyDialog.HideAsync();
    }

    protected async Task AddPasskey()
    {
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_newPasskeyName))
        {
            _errorMessage = "Please enter a name for your passkey.";
            return;
        }

        try
        {
            // Call JavaScript to initiate passkey creation with extended timeout (3 minutes)
            // Cloud password managers like Google Password Manager may need extra time
            // Note: If using Google Password Manager, ensure you have a stable internet connection
            // and that Google's services are accessible
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await JS.InvokeVoidAsync("passkeyHelper.registerPasskey", cts.Token, _newPasskeyName, _currentUser.Id);
            await CloseAddPasskeyDialog();
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Passkey registration timed out after 3 minutes");
            _errorMessage = "Passkey registration timed out. If using a cloud password manager (e.g., Google Password Manager), please check your internet connection and try again. Alternatively, try Windows Hello or a security key.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding passkey");
            _errorMessage = "Failed to add passkey. Please try again.";
        }
    }

    protected async Task ShowRenameDialog(Passkey passkey)
    {
        _selectedPasskey = passkey;
        _renamePasskeyName = passkey.Name;
        await _renamePasskeyDialog.ShowAsync();
    }

    protected async Task CloseRenameDialog()
    {
        await _renamePasskeyDialog.HideAsync();
    }

    protected async Task RenamePasskey()
    {
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_renamePasskeyName))
        {
            _errorMessage = "Please enter a new name.";
            return;
        }

        try
        {
            var success = await PasskeyService.RenamePasskeyAsync(_currentUser.Id, _selectedPasskey.Id, _renamePasskeyName);
            
            if (success)
            {
                _successMessage = "Passkey renamed successfully.";
                await LoadPasskeys();
                await CloseRenameDialog();
            }
            else
            {
                _errorMessage = "Failed to rename passkey.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error renaming passkey");
            _errorMessage = "An error occurred while renaming the passkey.";
        }
    }

    protected async Task ShowDeleteConfirmDialog(Passkey passkey)
    {
        _selectedPasskey = passkey;
        await _deletePasskeyDialog.ShowAsync();
    }

    protected async Task CloseDeletePasskeyDialog()
    {
        await _deletePasskeyDialog.HideAsync();
    }

    protected async Task DeletePasskey()
    {
        _errorMessage = string.Empty;

        try
        {
            var success = await PasskeyService.DeletePasskeyAsync(_currentUser.Id, _selectedPasskey.Id);
            
            if (success)
            {
                _successMessage = "Passkey deleted successfully.";
                await LoadPasskeys();
                await CloseDeletePasskeyDialog();
            }
            else
            {
                _errorMessage = "Failed to delete passkey.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting passkey");
            _errorMessage = "An error occurred while deleting the passkey.";
        }
    }

    protected async Task ShowAccountClosureDialog()
    {
        _accountActionConfirmEmail = string.Empty;
        _errorMessage = string.Empty;
        await _accountClosureDialog.ShowAsync();
    }

    protected async Task CloseAccountClosureDialog()
    {
        await _accountClosureDialog.HideAsync();
    }

    /// <summary>
    /// Returns true if the user has an active subscription that hasn't been cancelled yet.
    /// </summary>
    protected bool HasActiveSubscription => _hasSubscription && !_endDate.HasValue;

    protected async Task ShowSuspendConfirmDialog()
    {
        if (HasActiveSubscription)
        {
            _errorMessage = "You cannot suspend your account with an active subscription. You must cancel your active subscription and then try again.";
            return;
        }
        await _accountClosureDialog.HideAsync();
        await _suspendAccountDialog.ShowAsync();
    }

    protected async Task CloseSuspendAccountDialog()
    {
        await _suspendAccountDialog.HideAsync();
    }

    protected async Task SuspendAccount()
    {
        _errorMessage = string.Empty;

        if (_accountActionConfirmEmail != _currentUser.Email)
        {
            _errorMessage = "Email does not match. Please enter your exact email address to confirm.";
            return;
        }

        try
        {
            _currentUser.IsSuspended = true;
            _currentUser.SuspendedAt = DateTime.UtcNow;
            _currentUser.ReceiveNewSongEmails = false; // Ensure no communications when suspended
            
            var result = await UserManager.UpdateAsync(_currentUser);
            
            if (result.Succeeded)
            {
                // Send account suspended email notification (only if email is available)
                var userEmail = _currentUser.Email;
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        var baseUrl = NavigationManager.BaseUri;
                        var userName = _currentUser.UserName ?? userEmail;
                        await AccountEmailService.SendAccountClosedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send account suspended email to user {UserId}", _currentUser.Id);
                        // Don't fail the suspension if email sending fails
                    }
                }
                
                await CloseSuspendAccountDialog();
                NavigationManager.NavigateTo("/logout", forceLoad: true);
            }
            else
            {
                _errorMessage = "Failed to suspend account: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error suspending account");
            _errorMessage = "An error occurred while suspending your account.";
        }
    }

    protected async Task ShowDeleteConfirmDialog()
    {
        if (HasActiveSubscription)
        {
            _errorMessage = "You cannot delete your account with an active subscription. You must cancel your active subscription and then try again.";
            return;
        }
        await _accountClosureDialog.HideAsync();
        await _deleteAccountDialog.ShowAsync();
    }

    protected async Task CloseDeleteAccountDialog()
    {
        await _deleteAccountDialog.HideAsync();
    }

    protected async Task DeleteAccount()
    {
        _errorMessage = string.Empty;

        if (_accountActionConfirmEmail != _currentUser.Email)
        {
            _errorMessage = "Email does not match. Please enter your exact email address to confirm.";
            return;
        }

        // Capture user info before deletion
        var userEmail = _currentUser.Email;
        var userName = _currentUser.UserName ?? userEmail ?? "User";
        var baseUrl = NavigationManager.BaseUri;

        try
        {
            // Delete creator personas and their images before account deletion
            var creatorId = await CreatorService.GetCreatorIdForUserAsync(_currentUser.Id);
            if (creatorId.HasValue)
            {
                try
                {
                    await CreatorPersonaService.DeleteAllPersonasForCreatorAsync(creatorId.Value);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to delete creator personas before account deletion for user {UserId}", _currentUser.Id);
                    // Don't block account deletion if persona cleanup fails
                }
            }

            var result = await UserManager.DeleteAsync(_currentUser);
            
            if (result.Succeeded)
            {
                // Send account deleted email notification (only if email is available)
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        await AccountEmailService.SendAccountDeletedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send account deleted email");
                        // Don't fail the deletion if email sending fails
                    }
                }
                
                await CloseDeleteAccountDialog();
                NavigationManager.NavigateTo("/logout", forceLoad: true);
            }
            else
            {
                _errorMessage = "Failed to delete account: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting account");
            _errorMessage = "An error occurred while deleting your account.";
        }
    }

    private async Task LoadSubscriptionStatus()
    {
        try
        {
            var response = await Http.GetFromJsonAsync<SubscriptionStatusResponse>("api/subscription/status");
            if (response != null)
            {
                _hasSubscription = response.HasSubscription;
                _subscriptionStatus = response.Status ?? "N/A";
                _monthlyPrice = response.MonthlyPrice;
                _startDate = response.StartDate;
                _endDate = response.EndDate;
                _nextBillingDate = response.NextBillingDate;
                _paypalSubscriptionId = response.PaypalSubscriptionId;
                _subscriptionPrice = response.SubscriptionPrice ?? "3.99";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading subscription status");
        }
    }

    protected async Task Subscribe()
    {
        if (!_agreeToTerms)
        {
            _errorMessage = "You must agree to the terms and conditions to subscribe.";
            return;
        }

        _subscribing = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var response = await Http.PostAsJsonAsync("api/subscription/create", new { AgreeToTerms = _agreeToTerms });
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateSubscriptionResponse>();
                
                if (!string.IsNullOrEmpty(result?.ApprovalUrl))
                {
                    // Redirect to PayPal for approval
                    NavigationManager.NavigateTo(result.ApprovalUrl, forceLoad: true);
                }
                else
                {
                    _errorMessage = "Failed to create subscription. Please try again.";
                    _subscribing = false;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _errorMessage = $"Failed to create subscription: {errorContent}";
                _subscribing = false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating subscription");
            _errorMessage = $"Error creating subscription: {ex.Message}";
            _subscribing = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    protected async Task CancelSubscription()
    {
        if (!await JS.InvokeAsync<bool>("confirm", "Are you sure you want to cancel your subscription? You will have access until the end of your current billing period."))
        {
            return;
        }

        _cancelling = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var response = await Http.PostAsync("api/subscription/cancel", null);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CancelSubscriptionResponse>();
                
                if (result?.Success == true)
                {
                    await LoadSubscriptionStatus();
                    _successMessage = $"Your subscription has been cancelled. You can continue to listen to unlimited music until {_endDate?.ToLocalTime().ToString("MMMM dd, yyyy h:mm tt")}.";
                }
                else
                {
                    _errorMessage = "Failed to cancel subscription. Please try again.";
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _errorMessage = $"Failed to cancel subscription: {errorContent}";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error cancelling subscription");
            _errorMessage = $"Error cancelling subscription: {ex.Message}";
        }
        finally
        {
            _cancelling = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task LoadCreatorStatus()
    {
        try
        {
            // Check maintenance window for Tax Bandits
            _showMaintenanceWarning = await AppSettingsService.ShouldShowTaxBanditsMaintenanceWarningAsync();
            if (_showMaintenanceWarning)
            {
                var startUtc = await AppSettingsService.GetTaxBanditsMaintenanceStartUtcAsync();
                var endUtc = await AppSettingsService.GetTaxBanditsMaintenanceEndUtcAsync();
                try
                {
                    var localInfo = await JS.InvokeAsync<ManageAccountMaintenanceInfo>("getMaintenanceLocalTime",
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
                _isActiveCreator = creator.IsActive;
                _creatorOnboardingStatus = creator.OnboardingStatus.ToString();
                _creatorTaxFormStatus = creator.TaxFormStatus.ToString();
                
                // Calculate TIN match cooldown remaining if status is Failed
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
                _creatorReferralUrl = null; // PayPal business account onboarding has been removed
                _creatorDisplayName = creator.DisplayName ?? string.Empty;
                _creatorBio = creator.Bio ?? string.Empty;
                _paypalEmail = creator.PayPalEmail ?? string.Empty;
                
                // For returning creators, use values from the creator table
                _creatorStreamQualifyingSeconds = creator.StreamQualifyingSeconds;
                _creatorStreamPayRateDisplay = creator.StreamPayRate * 1000;
                
                // Initialize edit fields
                _editCreatorDisplayName = _creatorDisplayName;
                _editCreatorBio = _creatorBio;
            }
            else
            {
                _isActiveCreator = false;
                _creatorOnboardingStatus = null;
                _creatorTaxFormStatus = null;
                
                // For new creators, use current app settings values
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
            
            // Validate display name length
            if (!string.IsNullOrEmpty(_editCreatorDisplayName) && _editCreatorDisplayName.Length > 20)
            {
                _creatorProfileMessage = "Display name must be 20 characters or less.";
                _creatorProfileSuccess = false;
                return;
            }
            
            await CreatorService.UpdateCreatorProfileAsync(creator.Id, _editCreatorDisplayName, _editCreatorBio);
            
            // Update local state
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
            // Validate email format
            if (string.IsNullOrWhiteSpace(_paypalEmail))
            {
                _errorMessage = "Please enter a payout email address.";
                return;
            }

            if (!_paypalEmail.Contains("@") || !_paypalEmail.Contains("."))
            {
                _errorMessage = "Please enter a valid email address.";
                return;
            }

            await using var context = await DbContextFactory.CreateDbContextAsync();
            var creator = await context.Creators.FirstOrDefaultAsync(s => s.UserId == _currentUser.Id);
            
            if (creator == null)
            {
                _errorMessage = "Creator record not found.";
                return;
            }

            creator.PayPalEmail = _paypalEmail;
            creator.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

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
            // Parse the location certification enum value
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
                AcknowledgmentAccepted = _acknowledgmentAccepted
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
            else if (result.IsActive)
            {
                _successMessage = "Congratulations! Your creator account is now active. You can start uploading music!";
                await LoadCreatorStatus();
            }
            else if (result.TaxFormPending)
            {
                NavigationManager.NavigateTo("/submittaxform");
                return;
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
                _successMessage = "Congratulations! Your creator account is now active. You can start uploading and selling your music!";
                _isActiveCreator = true;
                _creatorOnboardingStatus = "Completed";
            }
            else if (result.PaymentsReceivable || result.PrimaryEmailConfirmed)
            {
                _successMessage = "Your PayPal account is being verified. Please check back soon.";
                _creatorOnboardingStatus = "InProgress";
            }
            else
            {
                _errorMessage = "PayPal verification is not complete. Please ensure you've completed all steps in PayPal.";
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
            else
            {
                _errorMessage = result.ErrorMessage ?? "Failed to initiate tax form update.";
            }
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
        if (_stopSellingConfirmEmail != _userEmail)
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

    /// <summary>
    /// Handles webhook status updates received via SignalR.
    /// </summary>
    private void OnWebhookStatusReceived(WebhookStatusMessage message)
    {
        // Only process messages for the current user
        if (_currentUser == null || message.UserId != _currentUser.Id)
        {
            return;
        }

        InvokeAsync(async () =>
        {
            try
            {
                // Show toast notification
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

                // Reload the relevant data based on webhook type
                if (message.WebhookType.Contains("PayPal", StringComparison.OrdinalIgnoreCase) ||
                    message.WebhookType.Contains("Creator", StringComparison.OrdinalIgnoreCase) ||
                    message.WebhookType.Contains("TaxForm", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadCreatorStatus();
                }

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling webhook status update");
            }
        });
    }

    /// <summary>
    /// Disposes of the SignalR event handler subscription.
    /// </summary>
    public void Dispose()
    {
        if (_webhookStatusHandler != null)
        {
            WebhookStatusHubClient.OnWebhookStatusReceived -= _webhookStatusHandler;
        }
        _cooldownTimer?.Dispose();
    }
}

public class SubscriptionStatusResponse
{
    public bool HasSubscription { get; set; }
    public string Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string SubscriptionPrice { get; set; }
}

public class CreateSubscriptionResponse
{
    public bool Success { get; set; }
    public string SubscriptionId { get; set; }
    public string ApprovalUrl { get; set; }
}

public class CancelSubscriptionResponse
{
    public bool Success { get; set; }
    public DateTime? EndDate { get; set; }
}

public class StartCreatorOnboardingResponse
{
    public bool Success { get; set; }
    public bool IsActive { get; set; }
    public bool TaxFormPending { get; set; }
    public bool IsIneligible { get; set; }
}

public class CompleteCreatorOnboardingResponse
{
    public bool Success { get; set; }
    public bool IsActive { get; set; }
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }
}

public class ManageAccountMaintenanceInfo
{
    public string StartLocal { get; set; } = string.Empty;
    public string EndLocal { get; set; } = string.Empty;
    public string TimeZoneAbbreviation { get; set; } = string.Empty;
}
