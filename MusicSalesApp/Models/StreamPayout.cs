#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a payout to a creator for streams of their music.
/// Creators are paid $5 per 1000 streams. Minimum payout threshold is $5.
/// For US creators, no withholding is applied (unless subject to backup withholding at 24%).
/// For foreign creators, withholding is applied based on tax treaty rates or default 30%.
/// </summary>
public class StreamPayout
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the Creator receiving this payout
    /// </summary>
    public int CreatorId { get; set; }

    /// <summary>
    /// Navigation property to the Creator
    /// </summary>
    [ForeignKey(nameof(CreatorId))]
    public virtual Creator Creator { get; set; } = null!;

    /// <summary>
    /// Foreign key to the SongMetadata record for the song being paid out
    /// </summary>
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the SongMetadata
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public virtual SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// The date and time when this payout was processed
    /// </summary>
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The number of streams the creator is being paid for in this payout
    /// </summary>
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// The rate paid per stream (e.g., 0.005 for $5 per 1000 streams)
    /// </summary>
    [Column(TypeName = "decimal(10,6)")]
    public decimal RatePerStream { get; set; }

    /// <summary>
    /// The gross amount earned before any withholding (NumberOfStreams * RatePerStream).
    /// This is reported on 1099-NEC Box 1 for US creators.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// The withholding rate applied to this payout as a decimal (e.g., 0.30 for 30%).
    /// For US creators: 0 (or 0.24 if subject to backup withholding).
    /// For foreign creators: treaty rate or default 0.30.
    /// Snapshot from Creator.EffectiveWithholdingRate at payout time.
    /// </summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal WithholdingRate { get; set; } = 0m;

    /// <summary>
    /// The amount withheld for taxes (GrossAmount * WithholdingRate).
    /// Reported on 1099-NEC Box 4 for US creators if backup withholding applies.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal WithheldAmount { get; set; } = 0m;

    /// <summary>
    /// The net amount paid to the creator after withholding (GrossAmount - WithheldAmount).
    /// This is the actual amount sent via PayPal.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetAmount { get; set; }

    /// <summary>
    /// The PayPal transaction ID for this payout.
    /// This is the payout_batch_id returned from PayPal's Payouts API.
    /// </summary>
    [MaxLength(100)]
    public string? PayPalTransactionId { get; set; }

    /// <summary>
    /// The TaxBandits Form 1099 transaction ID for this payout.
    /// Only populated for US creators after the transaction is reported to TaxBandits.
    /// Used for tracking and reconciliation of 1099-NEC filings.
    /// </summary>
    [MaxLength(100)]
    public string? TaxBandits1099TransactionId { get; set; }

    /// <summary>
    /// The status of the TaxBandits Form 1099 transaction submission.
    /// Examples: "Transactions saved successfully", "Failed", "Pending", etc.
    /// Only populated for US creators.
    /// </summary>
    [MaxLength(200)]
    public string? TaxBanditsStatus { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
