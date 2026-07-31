using Microsoft.AspNetCore.Components;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class AdminPersonaManagementModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected string _successMessage = string.Empty;
    protected List<PersonaAdminViewModel> _allPersonas = new();

    // Disable/enable dialog
    protected bool _showStatusDialog = false;
    protected PersonaAdminViewModel _statusPersona = null;
    protected bool _isDisablingAction = false; // true = disabling, false = enabling
    protected string _statusReason = string.Empty;
    protected List<string> _statusValidationErrors = new();
    protected bool _isProcessingStatus = false;

    private int _adminUserId;
    private bool _hasLoaded = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoaded)
        {
            _hasLoaded = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var adminId = GetUserId(authState.User);
                if (adminId == null)
                {
                    _errorMessage = "Unable to determine admin user identity. Please sign out and sign in again.";
                    Logger.LogWarning("AdminPersonaManagement: unable to determine admin user id from authentication state.");
                    return;
                }
                _adminUserId = adminId.Value;
                await LoadPersonasAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load personas: {ex.Message}";
                Logger.LogError(ex, "AdminPersonaManagement: error loading personas");
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadPersonasAsync()
    {
        var personas = await CreatorPersonaService.GetAllPersonasAdminAsync();
        var songCounts = await CreatorPersonaService.GetPersonaSongCountsAsync(personas.Select(p => p.Id));

        var viewModels = new List<PersonaAdminViewModel>();
        foreach (var p in personas)
        {
            var vm = new PersonaAdminViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Bio = p.Bio ?? string.Empty,
                WebsiteUrl = p.WebsiteUrl ?? string.Empty,
                ImageBlobPath = p.ImageBlobPath ?? string.Empty,
                IsImageSquare = p.IsImageSquare,
                IsEnabled = p.IsEnabled,
                CreatorId = p.CreatorId,
                CreatorEmail = p.Creator?.User?.Email ?? string.Empty,
                CreatorName = p.Creator?.DisplayName ?? p.Creator?.User?.Email ?? string.Empty,
                SongCount = songCounts.GetValueOrDefault(p.Id, 0)
            };
            if (!string.IsNullOrEmpty(p.ImageBlobPath))
            {
                // The admin grid caps the thumbnail at 55 CSS px.
                vm.PersonaImageUrl = CreatorPersonaService.GetPersonaImageSasUrl(
                    p.ImageBlobPath, p.ImageVariantWidths, 55, TimeSpan.FromHours(1));
            }
            viewModels.Add(vm);
        }

        _allPersonas = viewModels;
    }

    protected void ShowDisableDialog(PersonaAdminViewModel persona)
    {
        _statusPersona = persona;
        _isDisablingAction = true;
        _statusReason = string.Empty;
        _statusValidationErrors.Clear();
        _showStatusDialog = true;
    }

    protected void ShowEnableDialog(PersonaAdminViewModel persona)
    {
        _statusPersona = persona;
        _isDisablingAction = false;
        _statusReason = string.Empty;
        _statusValidationErrors.Clear();
        _showStatusDialog = true;
    }

    protected void CancelStatusDialog()
    {
        _showStatusDialog = false;
        _statusPersona = null;
    }

    protected async Task ConfirmStatusChange()
    {
        _statusValidationErrors.Clear();

        if (string.IsNullOrWhiteSpace(_statusReason))
        {
            _statusValidationErrors.Add("A reason is required.");
            return;
        }

        _isProcessingStatus = true;
        try
        {
            var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
            bool success;

            if (_isDisablingAction)
            {
                success = await CreatorPersonaService.DisablePersonaAsync(
                    _statusPersona.Id, _adminUserId, _statusReason.Trim(), baseUrl);
            }
            else
            {
                success = await CreatorPersonaService.EnablePersonaAsync(
                    _statusPersona.Id, _adminUserId, _statusReason.Trim(), baseUrl);
            }

            if (success)
            {
                var action = _isDisablingAction ? "disabled" : "re-enabled";
                _successMessage = $"Persona '{_statusPersona.Name}' has been {action}. The creator has been notified.";
                await LoadPersonasAsync();
            }
            else
            {
                _errorMessage = "Failed to update persona status. The persona may not exist.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error updating persona status: {ex.Message}";
            Logger.LogError(ex, "AdminPersonaManagement: error changing status for persona {PersonaId}", _statusPersona?.Id);
        }
        finally
        {
            _isProcessingStatus = false;
            _showStatusDialog = false;
            _statusPersona = null;
            await InvokeAsync(StateHasChanged);
        }
    }
}
