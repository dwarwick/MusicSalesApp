using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Popups;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages.Auth;

public partial class ManageAccountModel : BlazorBase
{
    private const string DefaultAppleSubscriptionManagementUrl = "https://account.apple.com/account/manage/section/subscriptions";
    private const string DefaultGoogleSubscriptionManagementUrl = "https://play.google.com/store/account/subscriptions";

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
    protected bool _isOnTrial;
    protected DateTime? _trialEndDate;
    protected string _paypalSubscriptionId;
    protected string _billingSource;
    protected string _subscriptionPrice = "3.99";
    protected string _userTimeZoneId = UserTimeZoneDisplayHelper.UtcTimeZoneId;
    protected bool _agreeToTerms = false;
    protected bool _subscribing = false;
    protected bool _cancelling = false;
    
    // Account closure
    protected bool _hasPurchasedMusic = false;
    protected string _accountActionConfirmEmail = string.Empty;
    protected bool IsAccountActionConfirmEmailMatch =>
        string.Equals(_accountActionConfirmEmail?.Trim(), _currentUser?.Email, StringComparison.Ordinal);

    // Creator account deletion guard
    protected bool _isActiveCreator = false;
    
    // Dialogs
    protected SfDialog _addPasskeyDialog;
    protected SfDialog _renamePasskeyDialog;
    protected SfDialog _deletePasskeyDialog;
    protected SfDialog _accountClosureDialog;
    protected SfDialog _suspendAccountDialog;
    protected SfDialog _deleteAccountDialog;
    
    private ApplicationUser _currentUser;

    [Inject]
    private IAccountDeletionService AccountDeletionService { get; set; }

    [SupplyParameterFromQuery(Name = "success")]
    public bool? Success { get; set; }

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
                        // Load email preferences
                        _receiveNewSongEmails = _currentUser.ReceiveNewSongEmails;

                        await DetectAndPersistUserTimeZoneAsync();
                        
                        await LoadPasskeys();
                        await CheckPurchasedMusic();
                        await LoadSubscriptionStatus();
                        await LoadCreatorStatus();

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
                            _userTimeZoneId,
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
    protected bool HasActiveSubscription => _hasSubscription;
    protected bool HasCancelledSubscriptionAccess => _hasSubscription && IsNonRenewingSubscription;
    protected bool CanCreateNewSubscription => !_hasSubscription ||
        _subscriptionStatus == SubscriptionStatuses.Expired;
    protected string SubscribeButtonLabel => HasCancelledSubscriptionAccess || _subscriptionStatus == SubscriptionStatuses.Expired
        ? "Start New Subscription"
        : "Sign Up for Monthly Subscription";
    protected bool IsNonRenewingSubscription =>
        _hasSubscription && string.Equals(_subscriptionStatus, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
    protected bool ShouldUseExternalSubscriptionManagement =>
        string.Equals(_billingSource, BillingSources.Apple, StringComparison.Ordinal) ||
        string.Equals(_billingSource, BillingSources.GooglePlay, StringComparison.Ordinal);
    protected bool ShowCancelSubscriptionButton => HasActiveSubscription && !IsNonRenewingSubscription;
    protected string DisplaySubscriptionStatus => IsNonRenewingSubscription
        ? "Renews Off"
        : _isOnTrial
            ? "Free Trial Active"
        : _subscriptionStatus switch
        {
            SubscriptionStatuses.Active => "Active",
            SubscriptionStatuses.Expired => "Expired",
            SubscriptionStatuses.ApprovalPending => "Pending",
            _ => _subscriptionStatus
        };
    protected string SubscriptionEndDateLabel => HasActiveSubscription
        ? _isOnTrial
            ? "Trial Active Until"
            : IsNonRenewingSubscription
            ? "Access Until"
            : "Current Billing Period Ends"
        : "Ended On";
    protected string SubscriptionTimeZoneLabel => UserTimeZoneDisplayHelper.GetTimeZoneDisplayLabel(_userTimeZoneId, _endDate ?? _nextBillingDate ?? _startDate);
    protected string ActiveSubscriptionMessage => IsNonRenewingSubscription
        ? $"Your subscription has been canceled. It will not automatically renew. You will continue to enjoy subscription benefits until {FormatUserDateTimeWithTimeZone(_endDate)}."
        : _isOnTrial
            ? $"Your free trial is active until {FormatUserDateTimeWithTimeZone(_trialEndDate ?? _endDate)}. During the trial, you have full subscription benefits. After the trial, your subscription will automatically renew unless canceled."
        : _endDate.HasValue
            ? $"Your subscription is active and will automatically renew unless canceled. Your current billing period ends on {FormatUserDateTimeWithTimeZone(_endDate)}."
            : "You have an active subscription that will automatically renew unless canceled.";
    protected string SubscriptionManagementPrompt => string.Equals(_billingSource, BillingSources.Apple, StringComparison.Ordinal)
        ? IsAppleSandboxManagementUrl()
            ? "Sandbox Apple subscriptions are managed on the test device in Settings > Developer > Sandbox Account > Manage. Open Apple's sandbox instructions now?"
            : "Apple subscriptions must be managed with Apple. Open Apple's subscription management page now?"
        : "Google Play subscriptions should be managed in Google Play subscription settings. Open Google Play now?";

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

        if (!IsAccountActionConfirmEmailMatch)
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

        if (_isActiveCreator)
        {
            _errorMessage = "Active creators must stop being a creator from Creator / Artist Settings before deleting the account.";
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

        if (!IsAccountActionConfirmEmailMatch)
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
            var result = await AccountDeletionService.DeleteAccountAsync(_currentUser);
            
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
                _isOnTrial = response.IsOnTrial;
                _trialEndDate = response.TrialEndDate;
                _paypalSubscriptionId = response.PaypalSubscriptionId;
                _billingSource = response.BillingSource;
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
        if (ShouldUseExternalSubscriptionManagement)
        {
            if (!await JS.InvokeAsync<bool>("confirm", SubscriptionManagementPrompt))
            {
                return;
            }

            NavigationManager.NavigateTo(GetExternalSubscriptionManagementUrl(), forceLoad: true);
            return;
        }

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
                    _successMessage = $"Your subscription has been cancelled. You can continue to listen to unlimited music until {FormatUserDateTimeWithTimeZone(_endDate)}.";
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

    protected string GetExternalSubscriptionManagementUrl()
        => string.Equals(_billingSource, BillingSources.Apple, StringComparison.Ordinal)
            ? Configuration["AppleAppStore:SubscriptionManagementUrl"] ?? DefaultAppleSubscriptionManagementUrl
            : DefaultGoogleSubscriptionManagementUrl;

    protected bool IsAppleSandboxManagementUrl()
        => GetExternalSubscriptionManagementUrl().Contains("developer.apple.com", StringComparison.OrdinalIgnoreCase);

    protected string FormatUserDate(DateTime? value)
        => value.HasValue ? UserTimeZoneDisplayHelper.FormatDate(value.Value, _userTimeZoneId) : string.Empty;

    protected string FormatUserDateTimeWithTimeZone(DateTime? value)
        => value.HasValue ? UserTimeZoneDisplayHelper.FormatDateTimeWithTimeZone(value.Value, _userTimeZoneId) : string.Empty;

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
            Logger.LogWarning(ex, "Failed to detect user timezone for manage account; using stored or UTC fallback");
        }
    }

    protected async Task LoadCreatorStatus()
    {
        try
        {
            _isActiveCreator = _currentUser != null && await CreatorService.IsActiveCreatorAsync(_currentUser.Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading creator account deletion guard");
            _isActiveCreator = false;
        }
    }
}

public class SubscriptionStatusResponse
{
    public bool HasSubscription { get; set; }
    public bool IsOnTrial { get; set; }
    public string Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public DateTime? TrialStartDate { get; set; }
    public DateTime? TrialEndDate { get; set; }
    public DateTime? TrialConvertedAt { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string BillingSource { get; set; }
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
