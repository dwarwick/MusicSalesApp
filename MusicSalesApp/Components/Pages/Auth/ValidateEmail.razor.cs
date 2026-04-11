using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

#nullable enable

namespace MusicSalesApp.Components.Pages.Auth;

public partial class ValidateEmailModel : BlazorBase, IDisposable
{
    protected string errorMessage = string.Empty;
    protected string successMessage = string.Empty;
    protected string infoMessage = string.Empty;
    protected bool isSubmitting = false;
    protected bool canResendEmail = false;
    protected int secondsRemaining = 0;
    protected bool _emailAlreadyVerified = false;
    protected string _currentEmail = string.Empty;

    public string NewEmail { get; set; } = string.Empty;

    private System.Timers.Timer? countdownTimer;
    private bool disposed = false;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadDataAsync();
            }
            finally
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadDataAsync()
    {
        var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = auth.User;

        if (user.Identity == null || !user.Identity.IsAuthenticated)
        {
            NavigationManager.NavigateTo("/login", forceLoad: true);
            return;
        }

        var emailClaim = user.FindFirst(System.Security.Claims.ClaimTypes.Email);
        if (emailClaim == null)
        {
            errorMessage = "Could not determine your email address.";
            return;
        }

        _currentEmail = emailClaim.Value;
        NewEmail = _currentEmail;

        var isVerified = await AuthenticationService.IsEmailVerifiedAsync(_currentEmail);
        if (isVerified)
        {
            _emailAlreadyVerified = true;
            successMessage = "Your email is already verified!";
            return;
        }

        infoMessage = "Your email address is not verified. Please verify your email to get full access to the site.";
        await CheckResendAvailability();

        // Auto-send a fresh verification email if cooldown has passed
        if (canResendEmail)
        {
            var baseUrl = NavigationManager.BaseUri;
            var (sent, _) = await AuthenticationService.SendVerificationEmailAsync(_currentEmail, baseUrl);
            if (sent)
            {
                successMessage = "A new verification email has been sent to your inbox.";
                await CheckResendAvailability();
                StartCountdownTimer();
            }
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
            var (success, error) = await AuthenticationService.SendVerificationEmailAsync(_currentEmail, baseUrl);

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

        if (NewEmail.Equals(_currentEmail, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Please enter a different email address";
            return;
        }

        isSubmitting = true;
        try
        {
            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.UpdateEmailAsync(_currentEmail, NewEmail, baseUrl);

            if (success)
            {
                _currentEmail = NewEmail;
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

    protected void GoToHome()
    {
        NavigationManager.NavigateTo("/");
    }

    private async Task CheckResendAvailability()
    {
        var (canResend, remaining) = await AuthenticationService.CanResendVerificationEmailAsync(_currentEmail);
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

    protected string FormatRemainingTime(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return minutes > 0 ? $"{minutes}m {secs}s" : $"{secs}s";
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
