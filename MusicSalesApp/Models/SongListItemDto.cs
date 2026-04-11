namespace MusicSalesApp.Models;

/// <summary>
/// DTO returned by the songs list API endpoint for the MAUI Android app.
/// Contains all metadata needed to display a song card and play the song.
/// </summary>
public class SongListItemDto
{
    public int Id { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string AlbumArtUrl { get; set; }
    public string PersonaImageUrl { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public int StreamCount { get; set; }
    public double? TrackLengthSeconds { get; set; }
    /// <summary>
    /// The ApplicationUser.Id of the creator who uploaded this song.
    /// Used by mobile clients to detect "creator listening to own song" for playback rules.
    /// </summary>
    public int? CreatorUserId { get; set; }
}
