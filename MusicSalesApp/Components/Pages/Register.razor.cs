using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Pages;

public partial class RegisterModel : BlazorBase, IDisposable
{
    [SupplyParameterFromQuery(Name = "needsVerification")]
    public bool NeedsVerification { get; set; }

    [SupplyParameterFromQuery(Name = "email")]
    public string QueryEmail { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
    [Required]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string NewEmail { get; set; } = string.Empty;

    // Legal agreement checkboxes
    public bool AcceptTermsOfUse { get; set; } = false;
    public bool AcceptPrivacyPolicy { get; set; } = false;

    // Computed property to check if user can register
    protected bool CanRegister => AcceptTermsOfUse && AcceptPrivacyPolicy;

    // Dialog references
    protected SfDialog _termsDialog = default!;
    protected SfDialog _privacyDialog = default!;

    protected string errorMessage = string.Empty;
    protected string successMessage = string.Empty;
    protected string infoMessage = string.Empty;
    protected bool isSubmitting = false;
    protected bool showVerificationSection = false;
    protected bool canResendEmail = false;
    protected int secondsRemaining = 0;
    private System.Timers.Timer countdownTimer = null!;
    private bool disposed = false;

    protected override async Task OnInitializedAsync()
    {
        var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (auth.User.Identity != null && auth.User.Identity.IsAuthenticated)
        {
            // Check if authenticated but email not verified
            var emailClaim = auth.User.FindFirst(System.Security.Claims.ClaimTypes.Email);
            if (emailClaim != null)
            {
                var isVerified = await AuthenticationService.IsEmailVerifiedAsync(emailClaim.Value);
                if (!isVerified)
                {
                    Email = emailClaim.Value;
                    NewEmail = Email;
                    showVerificationSection = true;
                    infoMessage = "Your email address is not verified. Please verify your email to get full access to the site.";
                    await CheckResendAvailability();
                    return;
                }
            }
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }

        // Handle redirect from login for unverified users
        if (NeedsVerification && !string.IsNullOrEmpty(QueryEmail))
        {
            Email = QueryEmail;
            NewEmail = Email;
            showVerificationSection = true;
            infoMessage = "Your email address is not verified. Please verify your email to get full access to the site.";
            await CheckResendAvailability();
        }
    }

    protected async Task ShowTermsOfUse()
    {
        await _termsDialog.ShowAsync();
    }

    protected async Task CloseTermsDialog()
    {
        await _termsDialog.HideAsync();
    }

    protected async Task ShowPrivacyPolicy()
    {
        await _privacyDialog.ShowAsync();
    }

    protected async Task ClosePrivacyDialog()
    {
        await _privacyDialog.HideAsync();
    }

    protected async Task HandleSubmit(EditContext context)
    {
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;

        if (isSubmitting) return;
        isSubmitting = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password) || string.IsNullOrWhiteSpace(ConfirmPassword))
            {
                errorMessage = "All fields are required";
                return;
            }
            if (!new EmailAddressAttribute().IsValid(Email))
            {
                errorMessage = "Invalid email format";
                return;
            }
            if (Password != ConfirmPassword)
            {
                errorMessage = "Passwords do not match";
                return;
            }
            if (!AcceptTermsOfUse || !AcceptPrivacyPolicy)
            {
                errorMessage = "You must accept the Terms of Use and Privacy Policy to register";
                return;
            }
            // Call registration service
            var (success, err) = await AuthenticationService.RegisterAsync(Email, Password);
            if (!success)
            {
                errorMessage = err;
                return;
            }

            // Send verification email
            var baseUrl = NavigationManager.BaseUri;
            var (emailSent, emailError) = await AuthenticationService.SendVerificationEmailAsync(Email, baseUrl);
            
            if (!emailSent)
            {
                errorMessage = $"Account created but failed to send verification email: {emailError}";
                return;
            }

            // Send welcome email notification (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await AccountEmailService.SendAccountCreatedEmailAsync(
                        Email,
                        Email, // Use email as username since that's all we have at registration
                        baseUrl);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to send welcome email to {Email}", Email);
                }
            });

            // Show verification section
            NewEmail = Email;
            showVerificationSection = true;
            successMessage = "Registration successful! Please check your email to verify your account.";
            await CheckResendAvailability();
            StartCountdownTimer();
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    protected async Task ResendVerificationEmail()
    {
        if (isSubmitting) return;
        
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;
        isSubmitting = true;

        try
        {
            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.SendVerificationEmailAsync(Email, baseUrl);
            
            if (success)
            {
                successMessage = "Verification email sent! Please check your inbox.";
                await CheckResendAvailability();
                StartCountdownTimer();
            }
            else
            {
                errorMessage = error;
            }
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    protected async Task ChangeEmail()
    {
        if (isSubmitting) return;
        
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(NewEmail))
        {
            errorMessage = "Please enter a new email address";
            return;
        }

        if (!new EmailAddressAttribute().IsValid(NewEmail))
        {
            errorMessage = "Invalid email format";
            return;
        }

        if (NewEmail.Equals(Email, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Please enter a different email address";
            return;
        }

        isSubmitting = true;
        try
        {
            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.UpdateEmailAsync(Email, NewEmail, baseUrl);
            
            if (success)
            {
                Email = NewEmail;
                successMessage = $"Email updated to {NewEmail}. A new verification email has been sent.";
                await CheckResendAvailability();
                StartCountdownTimer();
            }
            else
            {
                errorMessage = error;
            }
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    private async Task CheckResendAvailability()
    {
        var (canResend, remaining) = await AuthenticationService.CanResendVerificationEmailAsync(Email);
        canResendEmail = canResend;
        secondsRemaining = remaining;
    }

    private void StartCountdownTimer()
    {
        countdownTimer?.Stop();
        countdownTimer?.Dispose();
        countdownTimer = null;
        
        if (secondsRemaining > 0)
        {
            countdownTimer = new System.Timers.Timer(1000);
            countdownTimer.Elapsed += async (sender, e) =>
            {
                secondsRemaining--;
                if (secondsRemaining <= 0)
                {
                    canResendEmail = true;
                    countdownTimer?.Stop();
                }
                await InvokeAsync(StateHasChanged);
            };
            countdownTimer.Start();
        }
    }

    /// <summary>
    /// Formats remaining time for display (compact format).
    /// </summary>
    protected string FormatRemainingTime(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return minutes > 0 ? $"{minutes}m {secs}s" : $"{secs}s";
    }

    /// <summary>
    /// Formats remaining time for accessibility/screen readers (full descriptive format).
    /// </summary>
    protected string FormatRemainingTimeForAccessibility(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return minutes > 0 
            ? $"{minutes} minute{(minutes != 1 ? "s" : "")} and {secs} second{(secs != 1 ? "s" : "")}"
            : $"{secs} second{(secs != 1 ? "s" : "")}";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                countdownTimer?.Stop();
                countdownTimer?.Dispose();
                countdownTimer = null;
            }
            disposed = true;
        }
    }
}

