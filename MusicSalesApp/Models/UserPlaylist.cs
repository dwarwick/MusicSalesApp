using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class UserPlaylist
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    [Required]
    public int PlaylistId { get; set; }

    [ForeignKey("PlaylistId")]
    public Playlist Playlist { get; set; }

    [Required]
    public int SongMetadataId { get; set; }

    [ForeignKey("SongMetadataId")]
    public SongMetadata SongMetadata { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Order within the playlist (lower values appear first).
    /// Assigned on insert (max + 1) and updated by user drag-reorder.
    /// </summary>
    public int SortOrder { get; set; }
}
