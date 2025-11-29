using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class CartItem
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

    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
