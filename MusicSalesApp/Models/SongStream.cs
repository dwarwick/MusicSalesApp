using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Records individual song stream events with metadata about who streamed the song.
/// Created each time a song is streamed (played for at least the qualifying seconds).
/// </summary>
public class SongStream
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the SongMetadata record that was streamed.
    /// </summary>
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the SongMetadata.
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public virtual SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// Foreign key to the Creator who owns the song.
    /// Null if the song was uploaded by the platform admin.
    /// </summary>
    public int? CreatorId { get; set; }

    /// <summary>
    /// Navigation property to the Creator.
    /// </summary>
    [ForeignKey(nameof(CreatorId))]
    public virtual Creator Creator { get; set; }

    /// <summary>
    /// Foreign key to the ApplicationUser who streamed the song.
    /// Null if the user was not authenticated.
    /// </summary>
    public int? StreamerUserId { get; set; }

    /// <summary>
    /// Navigation property to the ApplicationUser who streamed the song.
    /// </summary>
    [ForeignKey(nameof(StreamerUserId))]
    public virtual ApplicationUser StreamerUser { get; set; }

    /// <summary>
    /// The date and time when the stream was recorded, in UTC.
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
