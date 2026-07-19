using Microsoft.AspNetCore.Components;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages.Public
{
    /// <summary>
    /// Represents a playlist with its tracks.
    /// </summary>
    public class PlaylistInfo
    {
        public string PlaylistName { get; set; }
        public string CoverArtUrl { get; set; }
        public string CoverArtFileName { get; set; }
        public List<StorageFileInfo> Tracks { get; set; } = new List<StorageFileInfo>();
        public int MetadataId { get; set; } // ID of the first track's SongMetadata record
    }

    /// <summary>
    /// Lightweight SEO track info for static SSR rendering.
    /// </summary>
    public class SeoTrackInfo
    {
        public string Title { get; set; }
        public string ArtistName { get; set; }
        public string Genre { get; set; }
    }

    public partial class PlaylistPlayerModel : ComponentBase
    {
        [Parameter]
        public int? PlaylistId { get; set; }

        [Parameter]
        public int? RecommendedUserId { get; set; }

        [Parameter]
        public string ArtistName { get; set; }

        [Parameter]
        public int? CreatorId { get; set; }

        [Parameter]
        public string GenreName { get; set; }

        [SupplyParameterFromQuery(Name = "tip_status")]
        public string TipStatus { get; set; }

        [SupplyParameterFromQuery(Name = "token")]
        public string TipPayPalToken { get; set; }

        [Inject]
        private ISongMetadataService SongMetadataService { get; set; }

        [Inject]
        private ICreatorService CreatorService { get; set; }

        protected string _displayTitle = "";
        protected string _seoDescription;
        protected List<SeoTrackInfo> _seoTracks = new();

        protected override async Task OnInitializedAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(GenreName))
                {
                    var decoded = Uri.UnescapeDataString(GenreName);
                    _displayTitle = decoded;
                    _seoDescription = $"Listen to {decoded} music on StreamTunes — unlimited streaming.";

                    var songs = await SongMetadataService.GetByGenreAsync(decoded);
                    PopulateSeoTracks(songs);
                }
                else if (!string.IsNullOrEmpty(ArtistName))
                {
                    var decoded = Uri.UnescapeDataString(ArtistName);
                    _displayTitle = decoded;
                    _seoDescription = $"Listen to {decoded} on StreamTunes — unlimited music streaming.";

                    var songs = await SongMetadataService.GetByArtistNameAsync(decoded);
                    PopulateSeoTracks(songs);
                }
                else if (CreatorId.HasValue)
                {
                    var songs = await SongMetadataService.GetByCreatorIdAsync(CreatorId.Value);
                    if (songs?.Any() == true)
                    {
                        var first = songs.First();
                        _displayTitle = GetArtistName(first);
                        _seoDescription = $"Listen to {_displayTitle} on StreamTunes — unlimited music streaming.";
                        PopulateSeoTracks(songs);
                    }
                    else
                    {
                        _displayTitle = "Creator";
                    }
                }
                else if (RecommendedUserId.HasValue)
                {
                    _displayTitle = "Recommended For You";
                }
                else if (PlaylistId.HasValue)
                {
                    _displayTitle = "Playlist";
                }
                else
                {
                    _displayTitle = "Unknown Playlist";
                }
            }
            catch
            {
                // SEO metadata is best-effort; interactive component handles errors
            }
        }

        private void PopulateSeoTracks(IEnumerable<Models.SongMetadata> songs)
        {
            if (songs == null) return;
            _seoTracks = songs
                .Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath))
                .Select(s => new SeoTrackInfo
                {
                    Title = SongTitleHelper.GetEffectiveTitle(s.SongTitle, s.Mp3BlobPath, s.BlobPath),
                    ArtistName = GetArtistName(s),
                    Genre = s.Genre
                })
                .ToList();
        }

        private static string GetArtistName(Models.SongMetadata metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata.ArtistName))
                return metadata.ArtistName.Contains('@') ? metadata.ArtistName.Split('@')[0] : metadata.ArtistName;
            if (metadata.Creator != null && !string.IsNullOrWhiteSpace(metadata.Creator.DisplayName))
                return metadata.Creator.DisplayName;
            return null;
        }
    }
}
