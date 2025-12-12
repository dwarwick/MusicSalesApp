using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class OwnedSong
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    [Required]
    [MaxLength(500)]
    public string SongFileName { get; set; }

    public DateTime PurchasedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string PayPalOrderId { get; set; }

    // Foreign key to SongMetadata
    public int? SongMetadataId { get; set; }

    [ForeignKey("SongMetadataId")]
    public SongMetadata SongMetadata { get; set; }
}
