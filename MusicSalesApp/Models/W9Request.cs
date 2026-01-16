#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a W-9/W-8 tax form request sent via TaxBandits API.
/// Stores the response from the TaxBandits API for tracking and auditing purposes.
/// </summary>
public class W9Request
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the ApplicationUser who is the recipient of the W-9 request.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the ApplicationUser.
    /// </summary>
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser User { get; set; } = null!;

    /// <summary>
    /// The TaxBandits submission ID returned from the API.
    /// This is a unique identifier for the submission.
    /// </summary>
    [MaxLength(100)]
    public string? SubmissionId { get; set; }

    /// <summary>
    /// The email address used in the request (PayeeRef).
    /// </summary>
    [MaxLength(255)]
    [EmailAddress]
    public string? Email { get; set; }

    /// <summary>
    /// The status of the W-9 certificate from TaxBandits.
    /// Examples: ORDER_CREATED, COMPLETED, FAILED, etc.
    /// </summary>
    [MaxLength(50)]
    public string? Status { get; set; }

    /// <summary>
    /// Timestamp when the status was last updated by TaxBandits.
    /// </summary>
    public DateTime? StatusTimestamp { get; set; }

    /// <summary>
    /// The full JSON response from the TaxBandits API for auditing.
    /// </summary>
    public string? RawResponse { get; set; }

    /// <summary>
    /// Any error ID from TaxBandits if the request failed.
    /// </summary>
    [MaxLength(50)]
    public string? ErrorId { get; set; }

    /// <summary>
    /// Error message from TaxBandits if the request failed.
    /// </summary>
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When the request was created in our system.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the record was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Indicates if the W-9 form has been completed successfully.
    /// </summary>
    public bool IsCompleted { get; set; } = false;

    /// <summary>
    /// When the W-9 was completed (from webhook notification).
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}
