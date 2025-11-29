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

    protected override async Task OnInitializedAsync()
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
        
        if (!success && !string.IsNullOrEmpty(error))
        {
            errorMessage = error;
        }
    }
}
