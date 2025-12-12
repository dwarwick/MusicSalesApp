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
    public int OwnedSongId { get; set; }

    [ForeignKey("OwnedSongId")]
    public OwnedSong OwnedSong { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
