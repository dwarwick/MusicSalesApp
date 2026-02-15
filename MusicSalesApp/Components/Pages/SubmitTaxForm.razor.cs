#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public partial class SubmitTaxFormModel : BlazorBase
{
    protected bool _loading = true;
    protected string _errorMessage = string.Empty;
    private bool _hasLoadedData = false;
    private IJSObjectReference? _jsModule;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated != true)
                {
                    NavigationManager.NavigateTo("/login");
                    return;
                }

                // Get the transient token and configuration from the server
                Logger.LogInformation("Fetching tax form token from API");
                var response = await Http.GetFromJsonAsync<TaxFormTokenResponse>("api/creator/tax-form-token");

                if (response == null || !response.Success)
                {
                    Logger.LogWarning("Tax form token request failed: {Error}", response?.ErrorMessage ?? "null response");
                    _errorMessage = response?.ErrorMessage ?? "Failed to load tax form. Please return to your account page and try again.";
                    _loading = false;
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                Logger.LogInformation("Tax form token received successfully. BusinessId: {BusinessId}, UseSandbox: {UseSandbox}",
                    response.BusinessId, response.UseSandbox);

                _loading = false;
                await InvokeAsync(StateHasChanged);

                // Load JS module and initialize the Drop-in UI
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/SubmitTaxForm.razor.js");
                
                var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
                Logger.LogInformation("Initializing TaxBandits Drop-in UI with return URL: {ReturnUrl}", $"{baseUrl}/manage-account");
                await _jsModule.InvokeVoidAsync("initTaxForm",
                    response.TransientToken,
                    response.PayeeRef,
                    response.BusinessId,
                    response.UseSandbox,
                    $"{baseUrl}/manage-account",
                    DotNetObjectReference.Create(this));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading tax form");
                _errorMessage = "An error occurred while loading the tax form. Please return to your account page and try again.";
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    /// <summary>
    /// Called from JavaScript when the tax form submission completes or is cancelled.
    /// </summary>
    [JSInvokable]
    public void OnTaxFormComplete(string status)
    {
        Logger.LogInformation("Tax form completed with status: {Status}", status);
        NavigationManager.NavigateTo("/manage-account", forceLoad: true);
    }
}

public class TaxFormTokenResponse
{
    public bool Success { get; set; }
    public string? TransientToken { get; set; }
    public string? PayeeRef { get; set; }
    public string? BusinessId { get; set; }
    public bool UseSandbox { get; set; }
    public string? ErrorMessage { get; set; }
}
