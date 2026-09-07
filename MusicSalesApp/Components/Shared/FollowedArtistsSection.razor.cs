#nullable enable
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// "Artists you follow" inside Manage Account.
/// </summary>
/// <remarks>
/// A child component rather than more code in ManageAccountModel, which is already 1,264 lines.
/// It needs no @rendermode of its own - the host page is InteractiveServer, so children inherit
/// interactivity.
/// </remarks>
public partial class FollowedArtistsSectionModel : BlazorBase
{
    /// <summary>
    /// The signed-in listener. Supplied by the host page, which has already resolved it, rather
    /// than resolved again here - a second identity lookup during first render is how two
    /// operations end up on one scoped DbContext.
    /// </summary>
    [Parameter]
    public int? UserId { get; set; }

    protected bool _loading = true;
    protected string? _statusMessage;
    protected List<FollowedArtistDto> _artists = [];

    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData || UserId is null)
        {
            return;
        }

        _hasLoadedData = true;
        await ReloadAsync();
    }

    /// <summary>
    /// Re-reads the list. Called by the host page when the Artist messages section below changes
    /// something this one displays - the per-artist unread count.
    /// </summary>
    public Task RefreshAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        if (UserId is null)
        {
            return;
        }

        try
        {
            _artists = (await ArtistFollowService.GetFollowedArtistsAsync(UserId.Value)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load followed artists for user {UserId}.", UserId);
            _statusMessage = "We could not load the artists you follow.";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected static string FollowedSince(FollowedArtistDto artist) =>
        artist.FollowedDateUtc.ToString("MMM d, yyyy");

    protected static string ArtistUrl(string artistName) =>
        $"/artist/{Uri.EscapeDataString(artistName)}";

    protected static string SongUrl(string songTitle) =>
        $"/song/{Uri.EscapeDataString(songTitle)}";

    protected async Task ToggleReleaseNotificationsAsync(FollowedArtistDto artist)
    {
        var wanted = !artist.ReleaseNotificationsEnabled;

        await ApplyAsync(
            () => ArtistFollowService.SetArtistNotificationPreferencesAsync(
                artist.CreatorPersonaId, UserId!.Value, wanted, null),
            wanted
                ? $"New releases from {artist.ArtistName} turned on."
                : $"New releases from {artist.ArtistName} muted.");
    }

    protected async Task ToggleArtistMessagesAsync(FollowedArtistDto artist)
    {
        var wanted = !artist.ArtistMessagesEnabled;

        await ApplyAsync(
            () => ArtistFollowService.SetArtistNotificationPreferencesAsync(
                artist.CreatorPersonaId, UserId!.Value, null, wanted),
            wanted
                ? $"Messages from {artist.ArtistName} turned on."
                : $"Messages from {artist.ArtistName} muted.");
    }

    protected async Task UnfollowAsync(FollowedArtistDto artist)
    {
        await ApplyAsync(
            async () =>
            {
                await ArtistFollowService.SetFollowStateAsync(artist.CreatorPersonaId, UserId!.Value, false);
                return true;
            },
            $"You no longer follow {artist.ArtistName}.");
    }

    protected async Task ToggleBlockedAsync(FollowedArtistDto artist)
    {
        var wanted = !artist.IsBlocked;

        await ApplyAsync(
            () => ArtistFollowService.SetBlockedAsync(artist.CreatorPersonaId, UserId!.Value, wanted),
            wanted
                ? $"{artist.ArtistName} is blocked. They can no longer message you, and you no longer follow them."
                : $"{artist.ArtistName} is unblocked. You are not following them again unless you choose to.");
    }

    /// <summary>
    /// Runs one change, reports it in the listener's own words, and reloads.
    /// </summary>
    /// <remarks>
    /// Reload rather than patching the local list: block also unfollows, and a mute leaves the row
    /// otherwise untouched, so re-reading is the only way the row on screen always matches the row
    /// in the database.
    /// </remarks>
    private async Task ApplyAsync(Func<Task<bool>> change, string successMessage)
    {
        if (UserId is null)
        {
            return;
        }

        try
        {
            var applied = await change();
            _statusMessage = applied ? successMessage : "That change could not be saved.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change a follow for user {UserId}.", UserId);
            _statusMessage = "That change could not be saved.";
        }

        await ReloadAsync();
    }
}
