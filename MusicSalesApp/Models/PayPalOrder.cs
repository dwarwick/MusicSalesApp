using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class PayPalOrder
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    [Required]
    [MaxLength(100)]
    public string OrderId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "CREATED";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
