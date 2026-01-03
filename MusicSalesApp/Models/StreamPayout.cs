using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a payout to a seller for streams of their music.
/// Sellers are paid $5 per 1000 streams. Minimum payout threshold is $5.
/// </summary>
public class StreamPayout
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the Seller receiving this payout
    /// </summary>
    public int SellerId { get; set; }

    /// <summary>
    /// Navigation property to the Seller
    /// </summary>
    [ForeignKey(nameof(SellerId))]
    public virtual Seller Seller { get; set; }

    /// <summary>
    /// Foreign key to the SongMetadata record for the song being paid out
    /// </summary>
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the SongMetadata
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public virtual SongMetadata SongMetadata { get; set; }

    /// <summary>
    /// The date and time when this payout was processed
    /// </summary>
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The number of streams the seller is being paid for in this payout
    /// </summary>
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// The rate paid per stream (e.g., 0.005 for $5 per 1000 streams)
    /// </summary>
    [Column(TypeName = "decimal(10,6)")]
    public decimal RatePerStream { get; set; }

    /// <summary>
    /// The total amount paid to the seller for this song in this payout
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountPaid { get; set; }

    /// <summary>
    /// The PayPal transaction ID for this payout.
    /// This is the payout_batch_id returned from PayPal's Payouts API.
    /// </summary>
    [MaxLength(100)]
    public string PayPalTransactionId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
