using Microsoft.AspNetCore.Components;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages;

public partial class SongPlayerModel : ComponentBase
{
    [Parameter]
    public string SongTitle { get; set; }

    [Inject]
    private ISongMetadataService SongMetadataService { get; set; }

    protected string _displayTitle = "";
    protected string _artistName;
    protected string _genre;

    protected override async Task OnInitializedAsync()
    {
        if (string.IsNullOrWhiteSpace(SongTitle))
        {
            _displayTitle = "Unknown Song";
            return;
        }

        var decodedTitle = Uri.UnescapeDataString(SongTitle);
        _displayTitle = decodedTitle;

        try
        {
            var allMetadata = await SongMetadataService.GetAllAsync();

            var metadata = allMetadata.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.Mp3BlobPath) &&
                MusicFileExtensions.IsAudioFile(m.Mp3BlobPath) &&
                (Path.GetFileNameWithoutExtension(Path.GetFileName(m.Mp3BlobPath)).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(m.Mp3BlobPath).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(m.SongTitle) && m.SongTitle.Equals(decodedTitle, StringComparison.OrdinalIgnoreCase))));

            if (metadata != null)
            {
                _displayTitle = metadata.SongTitle ?? decodedTitle;
                _genre = metadata.Genre;

                if (!string.IsNullOrWhiteSpace(metadata.ArtistName))
                {
                    _artistName = metadata.ArtistName.Contains('@') ? metadata.ArtistName.Split('@')[0] : metadata.ArtistName;
                }
                else if (metadata.Creator != null && !string.IsNullOrWhiteSpace(metadata.Creator.DisplayName))
                {
                    _artistName = metadata.Creator.DisplayName;
                }
            }
        }
        catch
        {
            // SEO metadata is best-effort; interactive component handles errors
        }
    }
}
