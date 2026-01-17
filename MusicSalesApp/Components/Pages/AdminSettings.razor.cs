using MusicSalesApp.Components.Base;

#nullable enable

namespace MusicSalesApp.Components.Pages;

public class AdminSettingsModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected string? _successMessage = null;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    protected bool _hasLoadedData = false;

    // Settings fields
    protected decimal? _subscriptionPrice = null;
    protected decimal? _originalSubscriptionPrice = null;
    protected decimal? _streamPayRateDisplay = null; // Display as per 1000 streams (e.g., 5.00)
    protected decimal? _originalStreamPayRateDisplay = null;

    protected bool _hasChanges => _subscriptionPrice != _originalSubscriptionPrice 
                                   || _streamPayRateDisplay != _originalStreamPayRateDisplay;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load settings: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadSettingsAsync()
    {
        _subscriptionPrice = await AppSettingsService.GetSubscriptionPriceAsync();
        _originalSubscriptionPrice = _subscriptionPrice;
        
        // Convert stream pay rate from per-stream to per-1000-streams for display
        var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
        _streamPayRateDisplay = streamPayRate * 1000;
        _originalStreamPayRateDisplay = _streamPayRateDisplay;
    }

    protected void CancelChanges()
    {
        _subscriptionPrice = _originalSubscriptionPrice;
        _streamPayRateDisplay = _originalStreamPayRateDisplay;
        _validationErrors.Clear();
        _successMessage = null;
        StateHasChanged();
    }

    protected async Task SaveSettings()
    {
        _validationErrors.Clear();
        _successMessage = null;
        _isSaving = true;

        try
        {
            // Validation
            if (!_subscriptionPrice.HasValue || _subscriptionPrice.Value <= 0)
            {
                _validationErrors.Add("Subscription price must be greater than 0.");
            }

            if (_subscriptionPrice.HasValue && _subscriptionPrice.Value > 999.99m)
            {
                _validationErrors.Add("Subscription price cannot exceed $999.99.");
            }

            if (!_streamPayRateDisplay.HasValue || _streamPayRateDisplay.Value <= 0)
            {
                _validationErrors.Add("Stream pay rate must be greater than 0.");
            }

            if (_streamPayRateDisplay.HasValue && _streamPayRateDisplay.Value > 100)
            {
                _validationErrors.Add("Stream pay rate cannot exceed $100 per 1000 streams.");
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Save the subscription price
            await AppSettingsService.SetSubscriptionPriceAsync(_subscriptionPrice!.Value);

            // Save the stream pay rate (convert from per-1000-streams to per-stream)
            await AppSettingsService.SetStreamPayRateAsync(_streamPayRateDisplay!.Value / 1000);

            // Update the original values to reflect the saved state
            _originalSubscriptionPrice = _subscriptionPrice;
            _originalStreamPayRateDisplay = _streamPayRateDisplay;
            _successMessage = $"Settings saved successfully. Subscription price: ${_subscriptionPrice.Value:F2}, Stream pay rate: ${_streamPayRateDisplay.Value:F2} per 1000 streams";
            
            Logger.LogInformation("Settings updated - Subscription price: ${Price}, Stream pay rate: ${StreamRate} per 1000 streams", 
                _subscriptionPrice.Value, _streamPayRateDisplay.Value);
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving settings: {ex.Message}");
            Logger.LogError(ex, "Failed to save settings");
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }
}
