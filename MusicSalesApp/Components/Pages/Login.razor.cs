using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LoginModel : BlazorBase
{   
    [SupplyParameterFromQuery(Name = "error")]
    public string Error { get; set; }

    protected string errorMessage = string.Empty;
    protected string antiForgeryToken = string.Empty;
    protected bool isDevelopment = false;
    protected string usernameValue = string.Empty;
    protected bool reactivateAccount = false;

    protected override async Task OnInitializedAsync()
    {
        // Check if already logged in
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }

        // Get antiforgery token
        var httpContext = HttpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var tokens = Antiforgery.GetAndStoreTokens(httpContext);
            antiForgeryToken = tokens.RequestToken;
        }

        // Check if in development environment
        isDevelopment = Environment.IsDevelopment();

        // Display error message if present
        if (!string.IsNullOrEmpty(Error))
        {
            errorMessage = Error;
        }
    }

    protected async Task LoginWithPasskey()
    {
        if (string.IsNullOrWhiteSpace(usernameValue))
        {
            errorMessage = "Please enter your username or email first.";
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("passkeyHelper.loginWithPasskey", usernameValue);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initiating passkey login");
            errorMessage = "Failed to login with passkey.";
        }
    }
}
