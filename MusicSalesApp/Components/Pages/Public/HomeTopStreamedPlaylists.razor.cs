using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;

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
    /// When the ranking was taken, already formatted in the viewer's timezone; empty until the
    /// browser has been asked.
    /// </summary>
    /// <remarks>
    /// Worth showing because rank order is up to a day old while the stream counts on the playlist
    /// itself are live, so the two can disagree slightly. Naming the moment the ranking was taken is
    /// what makes that read as "a daily chart" rather than as a sorting bug.
    /// </remarks>
    protected string _rankedAtLocal = string.Empty;

    protected string SectionEyebrow =>
        string.IsNullOrEmpty(_rankedAtLocal) ? "Updated daily" : $"Ranked {_rankedAtLocal}";

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

            if (_songCounts.Count > 0)
            {
                await LoadRankedAtAsync();
            }
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Failed to load the most-streamed playlists for the home page.");
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

    /// <summary>
    /// Formats the generation time in the browser's timezone.
    /// </summary>
    /// <remarks>
    /// The browser rather than the server, because this section is shown to anonymous visitors and
    /// <c>UserTimeZoneDisplayHelper</c> resolves its timezone from the signed-in user's profile - so
    /// every signed-out reader would be told the time in UTC. Best-effort: if the interop fails the
    /// eyebrow simply stays on "Updated daily".
    /// </remarks>
    private async Task LoadRankedAtAsync()
    {
        var generatedAtUtc = await TopStreamedPlaylistService.GetLastGeneratedAtAsync();
        if (generatedAtUtc is null)
        {
            return;
        }

        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./js/local-time-helper.js");

            _rankedAtLocal = await module.InvokeAsync<string>(
                "formatUtcInLocalTime",
                DateTime.SpecifyKind(generatedAtUtc.Value, DateTimeKind.Utc).ToString("o"));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not format the top-streamed ranking time in local time.");
        }
    }

    protected static string PlaylistUrl(TopStreamedWindowDescriptor descriptor) =>
        $"/top-streamed/{descriptor.Window}";

    protected int SongCount(TopStreamedWindowDescriptor descriptor) =>
        _songCounts.TryGetValue(descriptor.Window, out var count) ? count : 0;
}
