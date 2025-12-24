using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public partial class LikeDislikeButtonsModel : BlazorBase
{
    [Parameter]
    public int SongMetadataId { get; set; }

    [Parameter]
    public EventCallback OnLikeStatusChanged { get; set; }

    [Parameter]
    public bool VerticalLayout { get; set; } = false;

    protected int _likeCount = 0;
    protected int _dislikeCount = 0;
    protected bool? _userLikeStatus = null; // true = liked, false = disliked, null = no preference
    protected bool _isProcessing = false;
    private int? _currentUserId = null;
    private int _previousSongMetadataId = 0;
    protected SfDialog _loginDialog;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadLikeCounts();
            await LoadUserLikeStatus();
            await InvokeAsync(StateHasChanged);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // Only reload data when SongMetadataId actually changes
        if (SongMetadataId > 0 && SongMetadataId != _previousSongMetadataId)
        {
            _previousSongMetadataId = SongMetadataId;
            await LoadLikeCounts();
            await LoadUserLikeStatus();
        }
    }

    private async Task LoadLikeCounts()
    {
        try
        {
            var counts = await SongLikeService.GetLikeCountsAsync(SongMetadataId);
            _likeCount = counts.likeCount;
            _dislikeCount = counts.dislikeCount;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading like counts for song {SongMetadataId}", SongMetadataId);
        }
    }

    private async Task LoadUserLikeStatus()
    {
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                var appUser = await UserManager.GetUserAsync(user);
                if (appUser != null)
                {
                    _currentUserId = appUser.Id;
                    _userLikeStatus = await SongLikeService.GetUserLikeStatusAsync(appUser.Id, SongMetadataId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading user like status for song {SongMetadataId}", SongMetadataId);
        }
    }

    protected async Task HandleLikeClick()
    {
        if (_isProcessing || _currentUserId == null)
            return;

        _isProcessing = true;
        try
        {
            var wasLiked = await SongLikeService.ToggleLikeAsync(_currentUserId.Value, SongMetadataId);
            
            // Update UI state
            _userLikeStatus = wasLiked ? true : null;
            
            // Reload counts
            await LoadLikeCounts();
            
            // Notify parent component
            await OnLikeStatusChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling like for song {SongMetadataId}", SongMetadataId);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    protected async Task HandleDislikeClick()
    {
        if (_isProcessing || _currentUserId == null)
            return;

        _isProcessing = true;
        try
        {
            var wasDisliked = await SongLikeService.ToggleDislikeAsync(_currentUserId.Value, SongMetadataId);
            
            // Update UI state
            _userLikeStatus = wasDisliked ? false : null;
            
            // Reload counts
            await LoadLikeCounts();
            
            // Notify parent component
            await OnLikeStatusChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling dislike for song {SongMetadataId}", SongMetadataId);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    protected string GetLikeButtonClass()
    {
        return _userLikeStatus == true ? "like-button active" : "like-button";
    }

    protected string GetDislikeButtonClass()
    {
        return _userLikeStatus == false ? "dislike-button active" : "dislike-button";
    }

    protected string GetLikeButtonTitle()
    {
        return _userLikeStatus == true ? "Remove like" : "Like this song";
    }

    protected string GetDislikeButtonTitle()
    {
        return _userLikeStatus == false ? "Remove dislike" : "Dislike this song";
    }

    protected void HandleUnauthenticatedLikeClick()
    {
        ShowLoginDialog();
    }

    protected void HandleUnauthenticatedDislikeClick()
    {
        ShowLoginDialog();
    }

    private async void ShowLoginDialog()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.ShowAsync();
        }
    }

    protected async void NavigateToLogin()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.HideAsync();
        }
        NavigationManager.NavigateTo("/login", forceLoad: true);
    }

    protected async void CloseLoginDialog()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.HideAsync();
        }
    }
}
