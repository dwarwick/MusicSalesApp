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

    protected bool _hasChanges => _subscriptionPrice != _originalSubscriptionPrice;

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
    }

    protected void CancelChanges()
    {
        _subscriptionPrice = _originalSubscriptionPrice;
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

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Save the setting
            await AppSettingsService.SetSubscriptionPriceAsync(_subscriptionPrice!.Value);

            // Update the original value to reflect the saved state
            _originalSubscriptionPrice = _subscriptionPrice;
            _successMessage = $"Settings saved successfully. New subscription price: ${_subscriptionPrice.Value:F2}";
            
            Logger.LogInformation("Subscription price updated to ${Price}", _subscriptionPrice.Value);
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
