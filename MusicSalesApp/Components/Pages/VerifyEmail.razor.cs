using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class VerifyEmailModel : BlazorBase
{
    [SupplyParameterFromQuery(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [SupplyParameterFromQuery(Name = "token")]
    public string Token { get; set; } = string.Empty;

    protected bool isLoading = true;
    protected bool isSuccess = false;
    protected string errorMessage = string.Empty;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await VerifyEmailAndSendWelcomeAsync();
            }
            finally
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task VerifyEmailAndSendWelcomeAsync()
    {
        if (string.IsNullOrEmpty(UserId) || string.IsNullOrEmpty(Token))
        {
            isLoading = false;
            errorMessage = "Invalid verification link. The link may be malformed or expired.";
            return;
        }

        var (success, error) = await AuthenticationService.VerifyEmailAsync(UserId, Token);
        isLoading = false;
        isSuccess = success;
        
        if (success)
        {
            // Send welcome email after successful verification
            await SendWelcomeEmailAsync();
        }
        else if (!string.IsNullOrEmpty(error))
        {
            errorMessage = error;
        }
    }

    private async Task SendWelcomeEmailAsync()
    {
        try
        {
            // Get the user's email to send the welcome email
            var user = await UserManager.FindByIdAsync(UserId);
            if (user != null && !string.IsNullOrEmpty(user.Email))
            {
                var baseUrl = NavigationManager.BaseUri;
                var userName = user.UserName ?? user.Email;
                await AccountEmailService.SendAccountCreatedEmailAsync(
                    user.Email,
                    userName,
                    baseUrl);
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't fail the verification
            Logger.LogError(ex, "Failed to send welcome email to user {UserId}", UserId);
        }
    }
}
