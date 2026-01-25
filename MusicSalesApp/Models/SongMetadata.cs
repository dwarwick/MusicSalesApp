using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Stores metadata for songs and albums that was previously stored in Azure Blob index tags
/// </summary>
public class SongMetadata
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Full path to the blob file (folder/filename) - DEPRECATED: Use Mp3BlobPath or ImageBlobPath instead
    /// </summary>
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the MP3 blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string Mp3BlobPath { get; set; }

    /// <summary>
    /// Full path to the image blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string ImageBlobPath { get; set; }

    /// <summary>
    /// File extension (.mp3, .jpg, .jpeg, .png) - DEPRECATED
    /// </summary>
    [MaxLength(10)]
    public string FileExtension { get; set; } = string.Empty;

    /// <summary>
    /// The name of the album that this file belongs to
    /// </summary>
    [MaxLength(200)]
    public string AlbumName { get; set; }

    /// <summary>
    /// Indicates whether this image file is the cover art for an album
    /// </summary>
    public bool IsAlbumCover { get; set; }

    /// <summary>
    /// The genre of the song (e.g., Rock, Country, Pop)
    /// </summary>
    [MaxLength(50)]
    public string Genre { get; set; }

    /// <summary>
    /// The display title of the song. If null, the title is derived from the file name.
    /// </summary>
    [MaxLength(200)]
    public string SongTitle { get; set; }

    /// <summary>
    /// The track number for an album track (1-based index)
    /// </summary>
    public int? TrackNumber { get; set; }

    /// <summary>
    /// The track length in seconds
    /// </summary>
    public double? TrackLength { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The number of times this song has been streamed (played for at least 30 seconds)
    /// </summary>
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// The number of streams at the time of the last payout.
    /// Used to calculate unpaid streams (NumberOfStreams - StreamsAtLastPayout).
    /// </summary>
    public int StreamsAtLastPayout { get; set; }

    /// <summary>
    /// Indicates whether this song or album should be displayed on the home page
    /// </summary>
    public bool DisplayOnHomePage { get; set; }

    /// <summary>
    /// Indicates whether this song is active and available for playback.
    /// Inactive songs are not displayed anywhere on the website and cannot be played.
    /// Songs are set to inactive when a creator deletes them or closes their account.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Foreign key to the Creator who uploaded this song.
    /// If null, the song was uploaded by the platform admin.
    /// </summary>
    public int? CreatorId { get; set; }

    /// <summary>
    /// Navigation property to the Creator who owns this song.
    /// </summary>
    public virtual Creator Creator { get; set; }

    /// <summary>
    /// Indicates whether this song is enabled and available for playback and playlists.
    /// Disabled songs are hidden from the media library, cannot be played, and are removed from playlists.
    /// Songs are disabled by admin when content violates terms or policies.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The reason for the most recent status change (enable or disable) by admin.
    /// Required when enabling or disabling a song.
    /// </summary>
    [MaxLength(1000)]
    public string StatusReason { get; set; }

    /// <summary>
    /// The artist name for this song. If set, overrides the creator's display name.
    /// Priority for display: ArtistName > Creator.DisplayName > Creator.User.Email
    /// </summary>
    [MaxLength(20)]
    public string ArtistName { get; set; }

    /// <summary>
    /// Gets the effective artist name using the priority:
    /// 1. SongMetadata.ArtistName
    /// 2. Creator.DisplayName
    /// 3. Creator.User.Email (part before @)
    /// </summary>
    public string GetEffectiveArtistName()
    {
        // Priority 1: ArtistName from SongMetadata
        if (!string.IsNullOrWhiteSpace(ArtistName))
        {
            return ArtistName;
        }

        // Priority 2: DisplayName from Creator
        if (Creator != null && !string.IsNullOrWhiteSpace(Creator.DisplayName))
        {
            return Creator.DisplayName;
        }

        // Priority 3: Email from Creator's User - use part before @ symbol
        if (Creator?.User?.Email != null)
        {
            return Creator.User.Email.Split('@')[0];
        }

        return string.Empty;
    }
}
