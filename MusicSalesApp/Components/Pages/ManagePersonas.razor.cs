using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class ManagePersonasModel : BlazorBase, IAsyncDisposable
{
    private const int PersonaImageOutputSize = 800; // Output size in pixels for cropped square images

    private long _maxImageFileSize = 10 * 1024 * 1024; // default 10MB

    protected bool _loading = true;
    protected string _errorMessage = string.Empty;
    protected string _successMessage = string.Empty;
    protected List<PersonaAdminViewModel> _personas = new();

    // Delete dialog
    protected bool _showDeleteDialog = false;
    protected PersonaAdminViewModel _personaToDelete;
    protected bool _isDeleting = false;

    // Edit dialog
    protected bool _showEditDialog = false;
    protected bool _isNewPersona = false;
    protected PersonaAdminViewModel _editingPersona;
    protected string _editName = string.Empty;
    protected string _editBio = string.Empty;
    protected string _editWebsiteUrl = string.Empty;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    protected IBrowserFile _personaImageFile = null;

    // Crop tool fields
    protected bool _showCropTool = false;
    protected bool _cropApplied = false;
    protected string _cropTargetBlobPath = null;
    protected int _cropZoom = 50;
    private IJSObjectReference _cropModule;

    private int? _creatorId;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var sizeMB = await AppSettingsService.GetMaxImageUploadSizeMBAsync();
                _maxImageFileSize = (long)sizeMB * 1024 * 1024;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ManagePersonas: Failed to load max image upload size. Using default.");
            }

            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        _creatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);
                        if (_creatorId.HasValue)
                        {
                            await LoadPersonasAsync();
                        }
                        else
                        {
                            _errorMessage = "You are not registered as a creator. Please complete creator onboarding first.";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load personas: {ex.Message}";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
                if (_personas.Count > 0)
                {
                    await CheckAllImageDimensions();
                }
            }
        }
    }

    protected async Task LoadPersonasAsync()
    {
        if (!_creatorId.HasValue) return;

        var personas = await CreatorPersonaService.GetPersonasByCreatorIdAsync(_creatorId.Value);

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
                SongCount = await CreatorPersonaService.GetPersonaSongCountAsync(p.Id)
            };
            if (!string.IsNullOrEmpty(p.ImageBlobPath))
            {
                vm.PersonaImageUrl = CreatorPersonaService.GetPersonaImageSasUrl(p.ImageBlobPath, TimeSpan.FromHours(1));
            }
            viewModels.Add(vm);
        }

        _personas = viewModels;
    }

    protected void ShowAddDialog()
    {
        _isNewPersona = true;
        _editingPersona = new PersonaAdminViewModel();
        _editName = string.Empty;
        _editBio = string.Empty;
        _editWebsiteUrl = string.Empty;
        _validationErrors.Clear();
        _personaImageFile = null;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _showEditDialog = true;
    }

    protected void EditPersona(PersonaAdminViewModel persona)
    {
        _isNewPersona = false;
        _editingPersona = persona;
        _editName = persona.Name;
        _editBio = persona.Bio;
        _editWebsiteUrl = persona.WebsiteUrl;
        _validationErrors.Clear();
        _personaImageFile = null;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _showEditDialog = true;
    }

    protected void CancelEdit()
    {
        _showEditDialog = false;
        _editingPersona = null;
        _showCropTool = false;
    }

    protected void ShowDeleteConfirmation(PersonaAdminViewModel persona)
    {
        _personaToDelete = persona;
        _showDeleteDialog = true;
    }

    protected void CancelDelete()
    {
        _showDeleteDialog = false;
        _personaToDelete = null;
    }

    protected async Task ConfirmDelete()
    {
        if (_personaToDelete == null || !_creatorId.HasValue) return;

        _isDeleting = true;
        try
        {
            var success = await CreatorPersonaService.DeletePersonaAsync(_personaToDelete.Id, _creatorId.Value);
            if (success)
            {
                _successMessage = $"Persona '{_personaToDelete.Name}' has been deleted.";
                await LoadPersonasAsync();
            }
            else
            {
                _errorMessage = "Failed to delete persona.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error deleting persona: {ex.Message}";
        }
        finally
        {
            _isDeleting = false;
            _showDeleteDialog = false;
            _personaToDelete = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task SaveEdit()
    {
        if (_editingPersona == null || !_creatorId.HasValue) return;

        _validationErrors.Clear();

        if (string.IsNullOrWhiteSpace(_editName))
        {
            _validationErrors.Add("Persona name is required.");
        }
        if (_editName?.Length > 200)
        {
            _validationErrors.Add("Persona name must be 200 characters or less.");
        }

        if (_validationErrors.Any())
            return;

        _isSaving = true;
        try
        {
            int personaId;
            if (_isNewPersona)
            {
                var created = await CreatorPersonaService.CreatePersonaAsync(
                    _creatorId.Value, _editName.Trim(), _editBio?.Trim(), _editWebsiteUrl?.Trim());
                personaId = created.Id;
            }
            else
            {
                personaId = _editingPersona.Id;
            }

            // Handle image upload
            string imageBlobPath = _isNewPersona ? null : (_editingPersona.ImageBlobPath ?? null);
            int? imageWidth = null;
            int? imageHeight = null;

            if (_cropApplied && !string.IsNullOrEmpty(_cropTargetBlobPath))
            {
                imageBlobPath = _cropTargetBlobPath;
                imageWidth = PersonaImageOutputSize;
                imageHeight = PersonaImageOutputSize;
            }
            else if (_personaImageFile != null)
            {
                var fileExtension = Path.GetExtension(_personaImageFile.Name).ToLowerInvariant();
                if (fileExtension != ".jpg" && fileExtension != ".jpeg" && fileExtension != ".png")
                {
                    _validationErrors.Add("Only JPEG and PNG images are supported.");
                    return;
                }

                if (_personaImageFile.Size > _maxImageFileSize)
                {
                    _validationErrors.Add($"Image file is too large. Maximum size is {_maxImageFileSize / (1024 * 1024)} MB.");
                    return;
                }

                var contentType = fileExtension == ".png" ? "image/png" : "image/jpeg";
                await using var stream = _personaImageFile.OpenReadStream(_maxImageFileSize);
                imageBlobPath = await CreatorPersonaService.UploadPersonaImageAsync(
                    personaId, _creatorId.Value, stream, contentType, fileExtension);
            }

            await CreatorPersonaService.UpdatePersonaAsync(
                personaId,
                _creatorId.Value,
                _editName.Trim(),
                _editBio?.Trim(),
                _editWebsiteUrl?.Trim(),
                imageBlobPath,
                imageWidth,
                imageHeight);

            _successMessage = _isNewPersona
                ? $"Persona '{_editName}' has been created."
                : $"Persona '{_editName}' has been updated.";
            await LoadPersonasAsync();
            _showEditDialog = false;
            _editingPersona = null;
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving persona: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void HandlePersonaImageUpload(InputFileChangeEventArgs e)
    {
        _personaImageFile = e.File;
        _cropApplied = false;
        _cropTargetBlobPath = null;
    }

    protected async Task OpenCropTool()
    {
        if (_editingPersona == null || string.IsNullOrEmpty(_editingPersona.ImageBlobPath)) return;

        _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");
        _showCropTool = true;
        _cropZoom = 50;
        await InvokeAsync(StateHasChanged);
        await Task.Delay(100);
        var proxyUrl = $"api/persona/image/{SafeEncodePath(_editingPersona.ImageBlobPath)}";
        await _cropModule.InvokeVoidAsync("initCropTool", "persona-crop-canvas", proxyUrl, null);
    }

    protected async Task OnCropZoomChanged(int value)
    {
        _cropZoom = value;
        if (_cropModule != null)
        {
            await _cropModule.InvokeVoidAsync("setZoom", value);
        }
    }

    protected async Task ApplyCrop()
    {
        if (_cropModule == null || _editingPersona == null) return;

        var targetPath = !string.IsNullOrEmpty(_editingPersona.ImageBlobPath)
            ? Path.ChangeExtension(_editingPersona.ImageBlobPath, ".png")
            : $"creator-{_creatorId}/persona-{_editingPersona.Id}.png";

        var uploadUrl = $"api/persona/upload-cropped-image?blobPath={Uri.EscapeDataString(targetPath)}";
        var success = await _cropModule.InvokeAsync<bool>("getCroppedImageAndUpload", uploadUrl);

        if (success)
        {
            _cropApplied = true;
            _cropTargetBlobPath = targetPath;
        }
        else
        {
            _validationErrors.Add("Failed to upload cropped image. Please try again.");
        }

        _showCropTool = false;
        await _cropModule.InvokeVoidAsync("disposeCropTool");
        await InvokeAsync(StateHasChanged);
    }

    protected async Task CancelCrop()
    {
        _showCropTool = false;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        if (_cropModule != null)
        {
            await _cropModule.InvokeVoidAsync("disposeCropTool");
        }
    }

    private async Task CheckAllImageDimensions()
    {
        try
        {
            _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");

            var toCheck = _personas
                .Where(p => !string.IsNullOrEmpty(p.PersonaImageUrl) && p.IsImageSquare == null)
                .ToList();
            if (toCheck.Count == 0) return;

            bool anyUpdated = false;
            foreach (var vm in toCheck)
            {
                try
                {
                    var dimensions = await _cropModule.InvokeAsync<ImageDimensions>("checkImageDimensions", vm.PersonaImageUrl);
                    if (dimensions != null)
                    {
                        vm.IsImageSquare = dimensions.Width == dimensions.Height;
                        anyUpdated = true;

                        var persona = await CreatorPersonaService.GetPersonaByIdAsync(vm.Id, _creatorId!.Value);
                        if (persona != null)
                        {
                            await CreatorPersonaService.UpdatePersonaAsync(
                                vm.Id, _creatorId.Value,
                                persona.Name, persona.Bio, persona.WebsiteUrl,
                                null, dimensions.Width, dimensions.Height);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to check image dimensions for persona {PersonaId}", vm.Id);
                }
            }

            if (anyUpdated)
                await InvokeAsync(StateHasChanged);
        }
        catch
        {
            // Best-effort
        }
    }

    protected record ImageDimensions(int Width, int Height);

    public async ValueTask DisposeAsync()
    {
        if (_cropModule != null)
        {
            try { await _cropModule.InvokeVoidAsync("disposeCropTool"); } catch { }
            try { await _cropModule.DisposeAsync(); } catch { }
        }
    }

    private static string SafeEncodePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;
        if (filePath.Contains("..") || filePath.Contains("~")) return string.Empty;
        var segments = filePath.Split('/');
        return string.Join("/", segments.Select(Uri.EscapeDataString));
    }
}
