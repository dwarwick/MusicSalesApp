using Microsoft.AspNetCore.Components;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components;

public partial class App : ComponentBase
{
    [Inject]
    private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    [Inject]
    private IOpenGraphService OpenGraphService { get; set; } = default!;

    private string metaHtml = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        await GenerateMetaTags();
    }

    private async Task GenerateMetaTags()
    {
        var path = HttpContextAccessor.HttpContext?.Request.Path.Value?.Trim('/') ?? string.Empty;
        
        // Check if this is a song or album page
        if (path.StartsWith("song/") && path.Count(x => x == '/') == 1)
        {
            var songTitle = path.Substring(5); // Remove "song/" prefix
            metaHtml = await OpenGraphService.GenerateSongMetaTagsAsync(songTitle);
        }
        else if (path.StartsWith("album/") && path.Count(x => x == '/') == 1)
        {
            var albumName = path.Substring(6); // Remove "album/" prefix
            metaHtml = await OpenGraphService.GenerateAlbumMetaTagsAsync(albumName);
        }
        else
        {
            metaHtml = string.Empty;
        }
    }
}
