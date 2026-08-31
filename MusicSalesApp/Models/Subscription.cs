using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

public class Subscription
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    [MaxLength(100)]
    public string PayPalSubscriptionId { get; set; }

    /// <summary>
    /// PayPal billing plan selected when this subscription was created.
    /// Null for legacy and non-PayPal subscriptions.
    /// </summary>
    [MaxLength(50)]
    public string PayPalPlanId { get; set; }

    /// <summary>
    /// Version of the configured PayPal web offer whose displayed terms were accepted.
    /// Null for legacy and non-PayPal subscriptions.
    /// </summary>
    public int? PayPalOfferVersion { get; set; }

    /// <summary>
    /// UTC timestamp when the subscriber accepted the plan, price, cadence, and trial terms.
    /// </summary>
    public DateTime? SubscriptionTermsAcceptedAt { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = SubscriptionStatuses.ApprovalPending;

    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    public DateTime? EndDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyPrice { get; set; }

    /// <summary>
    /// Store-provided price text such as a Google Play formatted price. Null when unavailable.
    /// </summary>
    [MaxLength(100)]
    public string StoreFormattedPrice { get; set; }

    /// <summary>
    /// Store-provided ISO currency code. Null when unavailable.
    /// </summary>
    [MaxLength(10)]
    public string StorePriceCurrencyCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CancelledAt { get; set; }

    public DateTime? LastPaymentDate { get; set; }

    public DateTime? NextBillingDate { get; set; }

    public DateTime? TrialStartDate { get; set; }

    public DateTime? TrialEndDate { get; set; }

    public DateTime? TrialConvertedAt { get; set; }

    public DateTime? TrialActivationEmailSentAt { get; set; }

    public DateTime? TrialConversionEmailSentAt { get; set; }

    /// <summary>
    /// When this subscription first reached ACTIVE, or null if it never has.
    ///
    /// <para>
    /// Exists because "has this user subscribed before" must not be inferred from the row's
    /// *current* status. HasPriorActivatedSubscriptionAsync used to read Status == ACTIVE or
    /// SUSPENDED, so anything that moved a row out of those - a cancellation webhook, the
    /// entitlement drift sweep - silently made the user eligible for another free trial. Set once
    /// and never cleared, by AppDbContext.SaveChanges.
    /// </para>
    /// </summary>
    public DateTime? ActivatedAtUtc { get; set; }

    /// <summary>
    /// When the PayPal entitlement drift sweep last verified this row against the provider.
    ///
    /// <para>
    /// Null sorts first, so rows never checked are covered before old ones are re-checked. Without
    /// it the sweep re-reads the same lowest ids every night and everything past its batch cap is
    /// never verified at all.
    /// </para>
    /// </summary>
    public DateTime? LastProviderCheckAtUtc { get; set; }

    [MaxLength(100)]
    public string TrialBasePlanId { get; set; }

    [MaxLength(100)]
    public string TrialOfferId { get; set; }

    [MaxLength(500)]
    public string TrialOfferTags { get; set; }

    public bool? GooglePlayAutoRenewEnabled { get; set; }

    /// <summary>
    /// Which provider bills this subscription. Use the <see cref="BillingSources"/> constants.
    ///
    /// <para>
    /// Required at the database, not merely by convention. An unlabelled row is not a tidiness
    /// problem: GetCurrentSubscriptionFromOtherProviderAsync asks for rows whose provider differs
    /// from a given one, and EF gives that comparison C# null semantics - so a NULL differs from
    /// every provider, including its own. Such a row reads as its own competitor, and
    /// reconciliation cancels the agreement at the provider as a redundant overlap. Rows created
    /// before this property existed were exactly that shape.
    /// </para>
    ///
    /// <para>
    /// The initialiser is a second line of defence: an insert path that forgets to set a provider
    /// stores PayPal rather than NULL. The project has nullable reference types disabled, so
    /// without <see cref="RequiredAttribute"/> EF would map this to a nullable column no matter
    /// what the initialiser says.
    /// </para>
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string BillingSource { get; set; } = BillingSources.PayPal;

    /// <summary>
    /// Google Play purchase token used for server-side verification. Null for PayPal subscriptions.
    /// </summary>
    [MaxLength(500)]
    public string GooglePlayPurchaseToken { get; set; }

    /// <summary>
    /// Google Play order ID for the subscription. Null for PayPal subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string GooglePlayOrderId { get; set; }

    /// <summary>
    /// Latest Apple App Store transaction ID for the subscription. Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreTransactionId { get; set; }

    /// <summary>
    /// Apple App Store original transaction ID that remains stable across renewals.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreOriginalTransactionId { get; set; }

    /// <summary>
    /// Apple App Store product identifier for the subscription. Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(150)]
    public string AppStoreProductId { get; set; }

    /// <summary>
    /// Optional app account token supplied by the client to correlate App Store renewals back to a user.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreAppAccountToken { get; set; }

    /// <summary>
    /// Apple environment for the subscription, for example Sandbox or Production.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(20)]
    public string AppStoreEnvironment { get; set; }
}
