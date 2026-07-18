using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Popups;
using System.Globalization;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages.Auth;

public partial class ManageAccountModel : BlazorBase
{
    private const string DefaultAppleSubscriptionManagementUrl = "https://account.apple.com/account/manage/section/subscriptions";
    private const string DefaultGoogleSubscriptionManagementUrl = "https://play.google.com/store/account/subscriptions";

    protected bool _loading = true;
    protected bool _isAuthenticated = false;
    private bool _hasLoadedData = false;
    private bool _subscriberManageAccountViewedTracked = false;
    private bool _subscriberSubscribeClickedTracked = false;
    
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
    protected string _storeFormattedPrice;
    protected string _storePriceCurrencyCode;
    protected DateTime? _startDate;
    protected DateTime? _endDate;
    protected DateTime? _nextBillingDate;
    protected bool _isOnTrial;
    protected DateTime? _trialEndDate;
    protected string _paypalSubscriptionId;
    protected string _billingSource;
    protected PayPalWebOfferQuote _payPalOfferQuote;
    protected string _userTimeZoneId = UserTimeZoneDisplayHelper.UtcTimeZoneId;
    protected bool _agreeToTerms = false;
    protected bool _subscribing = false;
    protected bool _cancelling = false;
    protected bool _refreshingSubscription = false;
    protected string _mismatchCorrelationId = string.Empty;
    
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

    [SupplyParameterFromQuery(Name = PayPalReturnQueryKeys.Success)]
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
                        if (NeedsSubscriptionOffer)
                        {
                            await LoadPayPalOfferQuoteAsync();
                        }
                        await LoadCreatorStatus();
                        await TrackSubscriberManageAccountViewedAsync();

                        // Handle return from PayPal subscription
                        if (Success.HasValue)
                        {
                            if (Success.Value)
                            {
                                try
                                {
                                    var activation = await PayPalSubscriptionManagementService.ActivateCurrentSubscriptionAsync(
                                        _currentUser,
                                        NavigationManager.BaseUri);
                                    if (activation.Success)
                                    {
                                        _successMessage = activation.IsTrial
                                            ? "Your free trial is active. You now have full access to stream every full song."
                                            : "Your subscription has been activated successfully!";
                                        await LoadSubscriptionStatus();
                                    }
                                    else
                                    {
                                        _errorMessage = activation.Error ?? "Failed to activate subscription. Please contact support.";
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
                                    var abandoned = await PayPalSubscriptionManagementService.AbandonPendingCheckoutAsync(_currentUser);
                                    _errorMessage = abandoned
                                        ? "Subscription setup was cancelled."
                                        : "PayPal checkout was cancelled, but we could not finish cleaning up the pending subscription. Please try again.";
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
    protected bool HasUnresolvedPayPalAgreement => !_hasSubscription
        && string.Equals(_billingSource, BillingSources.PayPal, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(_paypalSubscriptionId)
        && (_subscriptionStatus == SubscriptionStatuses.Active
            || _subscriptionStatus == SubscriptionStatuses.Suspended);
    protected bool NeedsSubscriptionOffer => !_hasSubscription && !HasUnresolvedPayPalAgreement;
    protected bool CanCreateNewSubscription => NeedsSubscriptionOffer && _payPalOfferQuote != null;
    protected bool HasFreeTrialOffer => _payPalOfferQuote?.HasFreeTrial == true;
    protected string TrialDurationDisplay => _payPalOfferQuote?.TrialDays is int trialDays
        ? $"{trialDays} {(trialDays == 1 ? "day" : "days")}"
        : string.Empty;
    protected string TrialHeadlineDisplay => _payPalOfferQuote?.TrialDays is int trialDays
        ? $"{trialDays}-Day"
        : string.Empty;
    protected string OfferPriceDisplay => FormatOfferPrice(_payPalOfferQuote);
    protected string OfferCadenceDisplay => FormatOfferCadence(_payPalOfferQuote);
    protected string OfferRegularTermsDisplay => _payPalOfferQuote == null
        ? string.Empty
        : $"{OfferPriceDisplay} {OfferCadenceDisplay}";
    protected string SubscribeButtonLabel => HasFreeTrialOffer
        ? $"Start My {TrialHeadlineDisplay} Free Trial"
        : _payPalOfferQuote?.IsFirstTimeSubscriber == false
            ? "Resubscribe with PayPal"
            : "Subscribe with PayPal";
    protected bool IsNonRenewingSubscription =>
        _hasSubscription && string.Equals(_subscriptionStatus, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
    protected bool ShouldUseExternalSubscriptionManagement =>
        string.Equals(_billingSource, BillingSources.Apple, StringComparison.Ordinal) ||
        string.Equals(_billingSource, BillingSources.GooglePlay, StringComparison.Ordinal);
    protected bool ShowCancelSubscriptionButton => HasActiveSubscription && !IsNonRenewingSubscription;
    protected string PayPalMismatchGuidance => string.Equals(
        _subscriptionStatus,
        SubscriptionStatuses.Suspended,
        StringComparison.OrdinalIgnoreCase)
        ? "PayPal reports that this billing agreement is suspended. Review the payment issue in PayPal, refresh the subscription, or stop the agreement."
        : "PayPal reports that this billing agreement is active, but StreamTunes cannot confirm a current paid period or free trial. Refresh the subscription so StreamTunes can check it again.";
    protected string PayPalAccountManagementUrl =>
        Configuration[AppSettingKeys.PayPalAccountManagementUrl] ?? string.Empty;
    protected bool CanOpenPayPal => Uri.TryCreate(
        PayPalAccountManagementUrl,
        UriKind.Absolute,
        out _);
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
        ? _isOnTrial
            ? $"Your free trial has been canceled, so you will not be charged. You still have full subscription benefits through {FormatUserDateTimeWithTimeZone(_trialEndDate ?? _endDate)}."
            : $"Your subscription has been canceled. It will not automatically renew. You will continue to enjoy subscription benefits until {FormatUserDateTimeWithTimeZone(_endDate)}."
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
                _storeFormattedPrice = response.StoreFormattedPrice;
                _storePriceCurrencyCode = response.StorePriceCurrencyCode;
                _startDate = response.StartDate;
                _endDate = response.EndDate;
                _nextBillingDate = response.NextBillingDate;
                _isOnTrial = response.IsOnTrial;
                _trialEndDate = response.TrialEndDate;
                _paypalSubscriptionId = response.PaypalSubscriptionId;
                _billingSource = response.BillingSource;
                _mismatchCorrelationId = HasUnresolvedPayPalAgreement && _currentUser != null
                    ? await PayPalSubscriptionManagementService.GetOpenMismatchCorrelationIdAsync(_currentUser.Id)
                        ?? string.Empty
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading subscription status");
        }
    }

    private async Task LoadPayPalOfferQuoteAsync()
    {
        try
        {
            _payPalOfferQuote = await PayPalSubscriptionManagementService.GetOfferQuoteAsync(_currentUser.Id);
            _agreeToTerms = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the PayPal web subscription offer");
            _payPalOfferQuote = null;
            _errorMessage = "Subscription options are temporarily unavailable. Please try again later.";
        }
    }

    protected async Task Subscribe()
    {
        if (!_agreeToTerms)
        {
            _errorMessage = "You must agree to the terms and conditions to subscribe.";
            return;
        }

        await TrackSubscriberSubscribeClickedAsync();

        _subscribing = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            if (_currentUser == null || _payPalOfferQuote == null)
            {
                _errorMessage = "Subscription options are temporarily unavailable. Please try again later.";
                _subscribing = false;
                return;
            }

            var result = await PayPalSubscriptionManagementService.CreateSubscriptionAsync(
                _currentUser,
                _agreeToTerms,
                _payPalOfferQuote.SettingsVersion,
                _payPalOfferQuote.PlanId,
                NavigationManager.BaseUri);

            if (result.Success && !string.IsNullOrWhiteSpace(result.ApprovalUrl))
            {
                NavigationManager.NavigateTo(result.ApprovalUrl, forceLoad: true);
            }
            else
            {
                _errorMessage = result.Error ?? "Failed to create subscription. Please try again.";
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

        var cancellingTrial = _isOnTrial;
        var accessEnd = _trialEndDate ?? _endDate;
        var confirmationMessage = accessEnd.HasValue
            ? cancellingTrial
                ? $"Are you sure you want to cancel your free trial? You will not be charged, and you will keep full access through {FormatUserDateTimeWithTimeZone(accessEnd)}."
                : $"Are you sure you want to cancel your subscription? You will keep full access through {FormatUserDateTimeWithTimeZone(accessEnd)}."
            : cancellingTrial
                ? "Are you sure you want to cancel your free trial? You will not be charged, and you will keep full access through the provider-confirmed end of the trial."
                : "Are you sure you want to cancel your subscription? You will keep full access through the provider-confirmed end of your current billing period.";

        if (!await JS.InvokeAsync<bool>("confirm", confirmationMessage))
        {
            return;
        }

        _cancelling = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var result = await PayPalSubscriptionManagementService.CancelSubscriptionAsync(
                _currentUser,
                NavigationManager.BaseUri);

            if (result.Success)
            {
                _endDate = result.EndDate ?? accessEnd;
                await LoadSubscriptionStatus();
                if (NeedsSubscriptionOffer && _payPalOfferQuote == null)
                {
                    await LoadPayPalOfferQuoteAsync();
                }
                var confirmedEnd = result.EndDate ?? _trialEndDate ?? _endDate;
                _successMessage = confirmedEnd.HasValue
                    ? cancellingTrial
                        ? $"Your free trial has been cancelled. You will not be charged, and you can continue streaming every full song through {FormatUserDateTimeWithTimeZone(confirmedEnd)}."
                        : $"Your subscription has been cancelled. You can continue to listen to unlimited music through {FormatUserDateTimeWithTimeZone(confirmedEnd)}."
                    : cancellingTrial
                        ? "Your free trial has been cancelled. You will not be charged, and full access continues through the provider-confirmed end of the trial."
                        : "Your subscription has been cancelled. Full access continues through the provider-confirmed end of the current billing period.";
            }
            else
            {
                _errorMessage = result.Error ?? "Failed to cancel subscription. Please try again.";
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

    protected async Task RefreshSubscription()
    {
        if (_currentUser == null || _refreshingSubscription)
        {
            return;
        }

        _refreshingSubscription = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var result = await PayPalSubscriptionManagementService.ResolveCurrentMismatchAsync(
                _currentUser,
                NavigationManager.BaseUri);

            await LoadSubscriptionStatus();
            _mismatchCorrelationId = result.CorrelationId ?? _mismatchCorrelationId;
            if (NeedsSubscriptionOffer)
            {
                await LoadPayPalOfferQuoteAsync();
            }

            switch (result.Status)
            {
                case PayPalMismatchResolutionStatuses.EntitlementRestored:
                    _successMessage = result.EntitlementEndDate.HasValue
                        ? $"Your PayPal subscription was refreshed and full streaming access is restored through {FormatUserDateTimeWithTimeZone(result.EntitlementEndDate)}."
                        : "Your PayPal subscription was refreshed and full streaming access is restored.";
                    break;
                case PayPalMismatchResolutionStatuses.Suspended:
                    _errorMessage = BuildMismatchMessage(
                        "PayPal still reports this billing agreement as suspended. Resolve the payment issue in PayPal, stop the agreement, or contact customer service.");
                    break;
                case PayPalMismatchResolutionStatuses.ActiveWithoutEntitlement:
                    _errorMessage = BuildMismatchMessage(
                        "PayPal reports this billing agreement as active, but it did not provide enough payment or trial evidence to restore streaming access. Please contact customer service.");
                    break;
                case PayPalMismatchResolutionStatuses.AgreementEnded:
                    _successMessage = "PayPal confirmed that the previous billing agreement has ended. You may now start a new subscription.";
                    break;
                case PayPalMismatchResolutionStatuses.NoAgreement:
                    _errorMessage = "No PayPal billing agreement was found for this account.";
                    break;
                default:
                    _errorMessage = BuildMismatchMessage(
                        result.Error ?? "PayPal could not be refreshed right now. Please try again.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing PayPal subscription mismatch");
            _errorMessage = "PayPal could not be refreshed right now. Please try again.";
        }
        finally
        {
            _refreshingSubscription = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void OpenPayPal()
    {
        if (CanOpenPayPal)
        {
            NavigationManager.NavigateTo(PayPalAccountManagementUrl, forceLoad: true);
        }
    }

    protected void ContactSupport()
    {
        var customerServiceEmail = Configuration[AppSettingKeys.EmailCustomerServiceEmail];
        if (string.IsNullOrWhiteSpace(customerServiceEmail))
        {
            _errorMessage = "Customer service contact information is temporarily unavailable.";
            return;
        }

        var reference = !string.IsNullOrWhiteSpace(_mismatchCorrelationId)
            ? _mismatchCorrelationId
            : _paypalSubscriptionId;
        var subject = Uri.EscapeDataString("StreamTunes PayPal subscription help");
        var body = Uri.EscapeDataString($"Please help with my PayPal subscription. Reference: {reference}");
        NavigationManager.NavigateTo($"mailto:{customerServiceEmail}?subject={subject}&body={body}", forceLoad: true);
    }

    private string BuildMismatchMessage(string message) =>
        string.IsNullOrWhiteSpace(_mismatchCorrelationId)
            ? message
            : $"{message} Reference: {_mismatchCorrelationId}";

    private static string FormatOfferPrice(PayPalWebOfferQuote offer)
    {
        if (offer == null)
        {
            return string.Empty;
        }

        var amount = offer.RegularPrice.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Equals(
            offer.CurrencyCode,
            PayPalSubscriptionDefaults.UsdCurrencyCode,
            StringComparison.OrdinalIgnoreCase)
            ? $"${amount}"
            : $"{amount} {offer.CurrencyCode.ToUpperInvariant()}";
    }

    protected string CurrentMonthlyPriceDisplay => !string.IsNullOrWhiteSpace(_storeFormattedPrice)
        ? _storeFormattedPrice
        : FormatPrice(_monthlyPrice, _storePriceCurrencyCode);

    private static string FormatPrice(decimal amount, string currencyCode)
    {
        var formattedAmount = amount.ToString("0.00", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(currencyCode)
            || string.Equals(
                currencyCode,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                StringComparison.OrdinalIgnoreCase)
            ? $"${formattedAmount}"
            : $"{formattedAmount} {currencyCode.ToUpperInvariant()}";
    }

    private static string FormatOfferCadence(PayPalWebOfferQuote offer)
    {
        if (offer == null)
        {
            return string.Empty;
        }

        var intervalCount = Math.Max(offer.IntervalCount, 1);
        var intervalUnit = offer.IntervalUnit.Trim().ToLowerInvariant();
        return intervalCount == 1
            ? $"per {intervalUnit}"
            : $"every {intervalCount} {intervalUnit}s";
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

    private async Task TrackSubscriberManageAccountViewedAsync()
    {
        if (_subscriberManageAccountViewedTracked || _currentUser == null)
        {
            return;
        }

        _subscriberManageAccountViewedTracked = true;

        await TrackSubscriberFunnelEventAsync(
            FunnelAnalyticsEvents.SubscriberManageAccountViewed,
            FunnelAnalyticsLabels.SubscriberManageAccount);

        await RecordSubscriberHistoryAsync(
            UserHistoryEventTypes.SubscriberManageAccountViewed,
            "Subscriber Manage Account viewed.");
    }

    private async Task TrackSubscriberSubscribeClickedAsync()
    {
        if (_subscriberSubscribeClickedTracked || _currentUser == null)
        {
            return;
        }

        _subscriberSubscribeClickedTracked = true;

        await TrackSubscriberFunnelEventAsync(
            FunnelAnalyticsEvents.SubscriberSubscribeClicked,
            FunnelAnalyticsLabels.SubscriberSubscribe);

        await RecordSubscriberHistoryAsync(
            UserHistoryEventTypes.SubscriberSubscribeClicked,
            "Subscriber subscription CTA clicked.");
    }

    private async Task TrackSubscriberFunnelEventAsync(string eventName, string label)
    {
        var payload = new Dictionary<string, object>
        {
            [FunnelAnalyticsParameters.Category] = FunnelAnalyticsLabels.SubscriberCategory,
            [FunnelAnalyticsParameters.Label] = label,
            [FunnelAnalyticsParameters.HasSubscription] = _hasSubscription,
            [FunnelAnalyticsParameters.SubscriptionStatus] = _subscriptionStatus ?? string.Empty
        };

        try
        {
            await JS.InvokeVoidAsync(
                GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName,
                eventName,
                payload);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send subscriber funnel analytics event {EventName}", eventName);
        }
    }

    private async Task RecordSubscriberHistoryAsync(string eventType, string description)
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
            Logger.LogWarning(ex, "Failed to record subscriber funnel history event {EventType}", eventType);
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
    public string StoreFormattedPrice { get; set; }
    public string StorePriceCurrencyCode { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string BillingSource { get; set; }
}
