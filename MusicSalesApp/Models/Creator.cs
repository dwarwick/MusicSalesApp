#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a creator who can upload and sell music on the platform.
/// Creators are onboarded through PayPal Partner Referrals and TaxBandits (W-9/W-8 forms),
/// and can receive payments for their music sales once both onboarding processes are complete.
/// </summary>
public class Creator
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the ApplicationUser who is the creator
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the ApplicationUser
    /// </summary>
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser User { get; set; } = null!;

    /// <summary>
    /// The PayPal email address where this creator receives payouts.
    /// This may be different from the creator's login email (User.Email).
    /// Used for PayPal Payouts API calls.
    /// </summary>
    [MaxLength(255)]
    [EmailAddress]
    public string? PayPalEmail { get; set; }

    /// <summary>
    /// The status of the creator's PayPal onboarding process.
    /// </summary>
    public CreatorOnboardingStatus OnboardingStatus { get; set; } = CreatorOnboardingStatus.NotStarted;

    /// <summary>
    /// The status of the creator's tax form (W-9/W-8) completion via TaxBandits.
    /// </summary>
    public TaxFormStatus TaxFormStatus { get; set; } = TaxFormStatus.NotStarted;

    /// <summary>
    /// When the creator completed their tax form (W-9/W-8).
    /// </summary>
    public DateTime? TaxFormCompletedAt { get; set; }

    /// <summary>
    /// The PayeeRef used in TaxBandits W-9/W-8 requests.
    /// This is typically the user's email address and is used to correlate webhook callbacks.
    /// </summary>
    [MaxLength(255)]
    public string? TaxBanditsPayeeRef { get; set; }

    /// <summary>
    /// Whether the creator can receive payments. Previously verified by PayPal during business
    /// account onboarding. Now set to true based on user affirmation (see PayPalAccountAffirmed).
    /// Kept for backward compatibility with existing queries.
    /// </summary>
    public bool PaymentsReceivable { get; set; } = false;

    /// <summary>
    /// Whether the creator's primary email was confirmed. Previously verified by PayPal during
    /// business account onboarding. Now set to true based on user affirmation.
    /// Kept for backward compatibility with existing queries.
    /// </summary>
    public bool PrimaryEmailConfirmed { get; set; } = false;

    /// <summary>
    /// Whether the creator has affirmed they have a valid PayPal account in good standing
    /// that can receive payouts. This replaces the business account onboarding flow.
    /// Note: This constraint is enforced in StreamPayoutService.ProcessCreatorPayoutAsync()
    /// which checks PayPalAccountAffirmed before sending payouts.
    /// </summary>
    public bool PayPalAccountAffirmed { get; set; } = false;

    /// <summary>
    /// The rate paid per stream in USD (e.g., 0.005 for $5 per 1000 streams).
    /// This is set when the creator is onboarded and locked in for the lifetime of the creator account.
    /// </summary>
    [Column(TypeName = "decimal(10,6)")]
    public decimal StreamPayRate { get; set; } = 0.005m;

    /// <summary>
    /// The number of continuous seconds of playback that qualifies as a stream.
    /// This is set when the creator is onboarded and locked in for the lifetime of the creator account.
    /// </summary>
    public int StreamQualifyingSeconds { get; set; } = 30;

    /// <summary>
    /// Display name for the creator (can be different from username).
    /// Maximum 20 characters for display on song cards.
    /// </summary>
    [MaxLength(20)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional bio or description for the creator.
    /// </summary>
    [MaxLength(1000)]
    public string? Bio { get; set; }

    /// <summary>
    /// When the creator record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the creator completed PayPal onboarding.
    /// </summary>
    public DateTime? OnboardedAt { get; set; }

    /// <summary>
    /// When the creator record was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the creator account is currently active and can sell music.
    /// When set to false, the creator's music will not be available in the Music Library or playlists.
    /// </summary>
    public bool IsActive { get; set; } = false;

    /// <summary>
    /// The tax residency type of the creator. US for W-9 filers, FOREIGN for W-8BEN filers.
    /// Derived from the completed tax form type.
    /// </summary>
    public TaxResidencyType TaxResidencyType { get; set; } = TaxResidencyType.Unknown;

    /// <summary>
    /// The ISO-2 country code of the creator's tax residency (e.g., "MX" for Mexico).
    /// Derived from the CitizenOfCountry field in W-8BEN FormData.
    /// Null for US creators.
    /// </summary>
    [MaxLength(2)]
    public string? TaxResidencyCountry { get; set; }

    /// <summary>
    /// The withholding rate as a decimal (e.g., 0.24 for 24%).
    /// For US creators: 0 (unless subject to backup withholding, then 0.24).
    /// For foreign creators: 0 (no withholding applied).
    /// Snapshot at tax form completion time - never recomputed.
    /// </summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal WithholdingRate { get; set; } = 0m;

    /// <summary>
    /// Whether the US creator is subject to backup withholding per their W-9 form.
    /// When true, WithholdingRate should be 0.24 (24%).
    /// Always false for foreign creators.
    /// </summary>
    public bool SubjectToBackupWithholding { get; set; } = false;

    /// <summary>
    /// The expiration date of the W-8 tax form.
    /// W-8 forms expire Dec 31 of the 3rd year after signing.
    /// Null for W-9 forms (no expiration).
    /// </summary>
    public DateTime? TaxFormExpirationDate { get; set; }

    /// <summary>
    /// The TaxBandits submission ID for the completed tax form.
    /// Used for reference and audit purposes.
    /// </summary>
    public Guid? TaxBanditsSubmissionId { get; set; }

    /// <summary>
    /// When the creator's tax information was last verified.
    /// Used to track when tax form data was captured from webhook.
    /// </summary>
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>
    /// The creator's location certification selection for tax eligibility purposes.
    /// </summary>
    public CreatorLocationCertification LocationCertification { get; set; } = CreatorLocationCertification.None;

    /// <summary>
    /// Whether the creator has accepted the acknowledgment certifying the accuracy of their location certification.
    /// </summary>
    public bool AcknowledgmentAccepted { get; set; } = false;

    /// <summary>
    /// The UTC date and time when the creator accepted the acknowledgment.
    /// </summary>
    public DateTime? AcknowledgmentDateTimeUtc { get; set; }

    /// <summary>
    /// The UTC date and time of the last failed Instant TIN Match.
    /// Used to enforce the 24-hour cooldown period between TIN match attempts.
    /// </summary>
    public DateTime? LastTinMatchFailedAt { get; set; }

    /// <summary>
    /// The error message from the last tax form validation failure (e.g., TaxBandits rejected
    /// the request before it reached the IRS). Cleared when the tax form status advances
    /// beyond Pending.
    /// </summary>
    [MaxLength(1000)]
    public string? LastTaxFormErrorMessage { get; set; }

    /// <summary>
    /// Checks if both PayPal and tax form onboarding are complete.
    /// </summary>
    [NotMapped]
    public bool IsFullyOnboarded => OnboardingStatus == CreatorOnboardingStatus.Completed 
                                    && TaxFormStatus == TaxFormStatus.Completed;

    /// <summary>
    /// Gets the effective withholding rate.
    /// For US creators: 0 or 0.24 (backup withholding).
    /// For foreign creators: 0 (no withholding).
    /// </summary>
    [NotMapped]
    public decimal EffectiveWithholdingRate => WithholdingRate;
}

/// <summary>
/// Represents the status of a creator's PayPal onboarding process.
/// </summary>
public enum CreatorOnboardingStatus
{
    /// <summary>
    /// Onboarding has not been started.
    /// </summary>
    NotStarted = 0,

    /// <summary>
    /// Referral link has been generated, waiting for creator to complete PayPal signup.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Creator has completed PayPal signup but additional verification may be needed.
    /// </summary>
    InProgress = 2,

    /// <summary>
    /// Creator has completed onboarding and can receive payments.
    /// </summary>
    Completed = 3,

    /// <summary>
    /// Onboarding was declined or failed.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Creator account has been suspended.
    /// </summary>
    Suspended = 5,

    /// <summary>
    /// Creator has revoked their consent to the platform via PayPal.
    /// This typically happens when the creator removes the platform's permissions in their PayPal account.
    /// </summary>
    ConsentRevoked = 6,

    /// <summary>
    /// Creator is not eligible for payouts (e.g., non-U.S. person performing creator activities in the U.S.).
    /// </summary>
    Ineligible = 7
}

/// <summary>
/// Represents the status of a creator's tax form (W-9/W-8BEN) completion via TaxBandits.
/// </summary>
public enum TaxFormStatus
{
    /// <summary>
    /// Tax form has not been started or requested.
    /// </summary>
    NotStarted = 0,

    /// <summary>
    /// Tax form request has been sent, waiting for creator to complete it.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Tax form has been completed successfully.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Tax form submission failed or was rejected.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Tax form is completed but TIN matching with IRS is still in progress.
    /// This typically takes up to 1 hour to complete.
    /// </summary>
    TinMatchInProgress = 4
}

/// <summary>
/// Represents the tax residency type of a creator, determined by the tax form they complete.
/// </summary>
public enum TaxResidencyType
{
    /// <summary>
    /// Tax residency has not been determined yet (tax form not completed).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// US tax resident - completed W-9 form.
    /// </summary>
    US = 1,

    /// <summary>
    /// Foreign (non-US) tax resident - completed W-8BEN or W-8BEN-E form.
    /// </summary>
    Foreign = 2
}

/// <summary>
/// Represents the creator's location certification for tax eligibility purposes.
/// </summary>
public enum CreatorLocationCertification
{
    /// <summary>
    /// No selection has been made yet.
    /// </summary>
    None = 0,

    /// <summary>
    /// The creator is a U.S. person for tax purposes.
    /// </summary>
    USPerson = 1,

    /// <summary>
    /// The creator is not a U.S. person and will perform all creator activities entirely outside the United States.
    /// </summary>
    NonUSPersonOutsideUS = 2,

    /// <summary>
    /// The creator is not a U.S. person and will perform some creator activities while physically present in the United States.
    /// This makes the creator ineligible for payouts.
    /// </summary>
    NonUSPersonInsideUS = 3
}
