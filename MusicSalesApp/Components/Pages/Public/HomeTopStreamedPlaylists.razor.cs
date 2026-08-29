using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;

#nullable enable

namespace MusicSalesApp.Components.Pages.Public;

/// <summary>
/// Code-behind for the home page's "Most Streamed" island.
///
/// <para>
/// Deliberately a separate island from <see cref="HomeUserPlaylistsModel"/> rather than a section
/// inside it. That one is gated on <c>_isAuthenticated</c> because everything it shows is personal;
/// these five are the same for every visitor, so they must render for signed-out ones too - and there
/// is no auth check anywhere in this file.
/// </para>
///
/// <para>
/// It is an island at all for the reason documented on <c>HomeUserPlaylists</c>: <c>Home.razor</c> is
/// static SSR and never calls <c>OnAfterRenderAsync</c>, which AGENTS.md mandates for anything
/// touching the DbContext.
/// </para>
/// </summary>
public partial class HomeTopStreamedPlaylistsModel : BlazorBase
{
    private bool _hasLoadedData;

    protected bool _loading = true;

    /// <summary>Song count per window key. A window with no songs is absent, and renders no tile.</summary>
    protected Dictionary<string, int> _songCounts = new();

    /// <summary>
    /// The playlists that actually have songs, in the order the descriptor table defines - Day, Week,
    /// Month, Year, All Time.
    /// </summary>
    protected IReadOnlyList<TopStreamedWindowDescriptor> VisiblePlaylists =>
        TopStreamedPlaylists.All
            .OrderBy(descriptor => descriptor.DisplayOrder)
            .Where(descriptor => _songCounts.ContainsKey(descriptor.Window))
            .ToList();

    protected bool HasPlaylists => VisiblePlaylists.Count > 0;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        try
        {
            _songCounts = await TopStreamedPlaylistService.GetCountsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the most-streamed playlists for the home page.");
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected static string PlaylistUrl(TopStreamedWindowDescriptor descriptor) =>
        $"/top-streamed/{descriptor.Window}";

    protected int SongCount(TopStreamedWindowDescriptor descriptor) =>
        _songCounts.TryGetValue(descriptor.Window, out var count) ? count : 0;
}
