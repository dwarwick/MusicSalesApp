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
    protected bool showReactivateCheckbox = false;

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
            // Show reactivate checkbox only if the error message indicates account suspension
            showReactivateCheckbox = Error.Contains("suspended", StringComparison.OrdinalIgnoreCase) || 
                                     Error.Contains("reactivate", StringComparison.OrdinalIgnoreCase);
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
            // Call JavaScript to initiate passkey login with extended timeout (2 minutes)
            // Google Password Manager may take longer than Windows Hello
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await JS.InvokeVoidAsync("passkeyHelper.loginWithPasskey", cts.Token, usernameValue);
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Passkey login timed out after 2 minutes");
            errorMessage = "Passkey login timed out. Please try again and complete the process more quickly.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initiating passkey login");
            errorMessage = "Failed to login with passkey.";
        }
    }
}
