using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class Playlist
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string PlaylistName { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property for playlist songs
    public ICollection<UserPlaylist> UserPlaylists { get; set; }
}
