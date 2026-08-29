using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// One song's place in one of the five global "most streamed" playlists, rebuilt nightly.
///
/// <para>
/// Deliberately shaped like <see cref="RecommendedPlaylist"/> - a flat table of ranked songs
/// materialised into a virtual playlist at read time - rather than as a <see cref="Playlist"/> row.
/// Two reasons, both hard blockers: <c>Playlist.UserId</c> is a required foreign key, so a playlist
/// belonging to nobody has no home there without inventing a system user; and
/// <c>PlaylistCleanupService</c> deletes every <c>UserPlaylists</c> row belonging to a lapsed
/// subscriber, which would silently empty a shared playlist the first time anyone's subscription
/// lapsed.
/// </para>
/// </summary>
public class TopStreamedPlaylistEntry
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Which playlist this row belongs to - a <c>MusicSalesApp.Common.Helpers.TopStreamedWindows</c>
    /// constant. Never write a literal here.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Window { get; set; } = string.Empty;

    /// <summary>
    /// The song holding this position.
    /// </summary>
    [Required]
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the song metadata.
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// Rank within the playlist, 1 being the most streamed. Both players read the playlist in this order
    /// and must not re-sort it.
    /// </summary>
    [Required]
    public int DisplayOrder { get; set; }

    /// <summary>
    /// The stream count this row was ranked on - streams inside the window, or the lifetime total for
    /// the all-time playlist.
    ///
    /// <para>
    /// Stored rather than recomputed so the ranking that produced a playlist stays inspectable after the
    /// window has moved on. Note it is <b>not</b> what the player displays: the player's counter is
    /// the live lifetime count, kept current by SignalR.
    /// </para>
    /// </summary>
    [Required]
    public int StreamCount { get; set; }

    /// <summary>
    /// When the playlist this row belongs to was last rebuilt.
    /// </summary>
    [Required]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
