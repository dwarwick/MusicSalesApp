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
    private bool _needsDataReload = false;
    protected SfDialog _loginDialog;

    protected override void OnParametersSet()
    {
        // Track parameter changes; actual DB work happens in OnAfterRenderAsync
        if (SongMetadataId > 0 && SongMetadataId != _previousSongMetadataId)
        {
            _previousSongMetadataId = SongMetadataId;
            _needsDataReload = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if ((firstRender || _needsDataReload) && SongMetadataId > 0)
        {
            _needsDataReload = false;
            await LoadLikeCounts();
            await LoadUserLikeStatus();
            await InvokeAsync(StateHasChanged);
        }
        else if (_needsDataReload && SongMetadataId <= 0)
        {
            // SongMetadataId not yet populated — clear stale data and wait
            _needsDataReload = false;
            _likeCount = 0;
            _dislikeCount = 0;
            _userLikeStatus = null;
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
                var userId = GetUserId(user);
                if (userId.HasValue)
                {
                    _currentUserId = userId.Value;
                    _userLikeStatus = await SongLikeService.GetUserLikeStatusAsync(userId.Value, SongMetadataId);
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
            
            // Sync Liked Songs playlist
            await PlaylistService.SyncLikedSongsPlaylistAsync(_currentUserId.Value);
            
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
            
            // Sync Liked Songs playlist (removing liked songs when disliked)
            await PlaylistService.SyncLikedSongsPlaylistAsync(_currentUserId.Value);
            
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

    protected async Task HandleUnauthenticatedClick()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.ShowAsync();
        }
        else
        {
            Logger.LogWarning("Login dialog reference is null when attempting to show dialog");
        }
    }

    protected async Task NavigateToLogin()
    {
        await HideLoginDialog();
        NavigationManager.NavigateTo("/login", forceLoad: true);
    }

    protected async Task CloseLoginDialog()
    {
        await HideLoginDialog();
    }

    private async Task HideLoginDialog()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.HideAsync();
        }
    }
}
