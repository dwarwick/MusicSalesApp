#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for PayPal Partner Referrals API operations.
/// Used for onboarding creators to the platform.
/// </summary>
public interface IPayPalPartnerService
{
    /// <summary>
    /// Creates a partner referral (onboarding link) for a user to become a creator.
    /// </summary>
    /// <param name="userId">The ID of the user to onboard.</param>
    /// <param name="email">The email address of the user.</param>
    /// <returns>The referral URL for the user to complete PayPal onboarding, or null if failed.</returns>
    Task<PartnerReferralResult?> CreatePartnerReferralAsync(int userId, string email);

    /// <summary>
    /// Gets the merchant onboarding status from PayPal to check if onboarding is complete.
    /// </summary>
    /// <param name="partnerId">The partner merchant ID (platform's merchant ID).</param>
    /// <param name="merchantId">The merchant ID of the creator being onboarded.</param>
    /// <returns>The onboarding status information.</returns>
    Task<MerchantOnboardingStatus?> GetMerchantStatusAsync(string partnerId, string merchantId);

    /// <summary>
    /// Gets the merchant onboarding status using the tracking ID.
    /// </summary>
    /// <param name="trackingId">The tracking ID used during partner referral creation.</param>
    /// <returns>The onboarding status information.</returns>
    Task<MerchantOnboardingStatus?> GetMerchantStatusByTrackingIdAsync(string trackingId);

    /// <summary>
    /// Issues a refund for an order.
    /// </summary>
    /// <param name="captureId">The PayPal capture ID to refund.</param>
    /// <param name="amount">The amount to refund (null for full refund).</param>
    /// <param name="reason">The reason for the refund.</param>
    /// <returns>True if refund was successful.</returns>
    Task<RefundResult> IssueRefundAsync(string captureId, decimal? amount = null, string? reason = null);

    /// <summary>
    /// Creates a multi-party order where the creator is the merchant of record.
    /// Platform fee is split using PayPal's payment platform partner fee.
    /// </summary>
    /// <param name="creator">The creator who is the merchant of record.</param>
    /// <param name="items">The items being purchased.</param>
    /// <param name="totalAmount">The total amount of the order.</param>
    /// <param name="platformFee">The platform fee amount.</param>
    /// <returns>The created order details.</returns>
    Task<MultiPartyOrderResult?> CreateMultiPartyOrderAsync(Creator creator, IEnumerable<OrderItem> items, decimal totalAmount, decimal platformFee);

    /// <summary>
    /// Creates a multi-creator order with multiple purchase units (one per creator).
    /// Supports up to 10 creators per transaction. Each creator is the merchant of record for their items.
    /// </summary>
    /// <param name="creatorOrders">Dictionary mapping creator to their items and amounts.</param>
    /// <returns>The created order details with all creator merchant IDs.</returns>
    Task<MultiCreatorOrderResult?> CreateMultiCreatorOrderAsync(Dictionary<Creator, (IEnumerable<OrderItem> Items, decimal Amount, decimal PlatformFee)> creatorOrders);

    /// <summary>
    /// Captures a multi-party order payment.
    /// </summary>
    /// <param name="payPalOrderId">The PayPal order ID to capture.</param>
    /// <returns>The capture result.</returns>
    Task<CaptureResult> CaptureMultiPartyOrderAsync(string payPalOrderId);
}

/// <summary>
/// Result of creating a partner referral.
/// </summary>
public class PartnerReferralResult
{
    public string ReferralUrl { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Status information for a merchant's onboarding.
/// </summary>
public class MerchantOnboardingStatus
{
    public string MerchantId { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }
    public List<string> Products { get; set; } = new();
    public List<OAuthIntegration> OAuthIntegrations { get; set; } = new();
}

/// <summary>
/// OAuth integration status for a creator.
/// </summary>
public class OAuthIntegration
{
    public string IntegrationType { get; set; } = string.Empty;
    public string IntegrationMethod { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> OAuthThirdParty { get; set; } = new();
}

/// <summary>
/// Result of a refund operation.
/// </summary>
public class RefundResult
{
    public bool Success { get; set; }
    public string? RefundId { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of creating a multi-party order.
/// </summary>
public class MultiPartyOrderResult
{
    public string OrderId { get; set; } = string.Empty;
    public string? ApprovalUrl { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of creating a multi-creator order with multiple purchase units.
/// </summary>
public class MultiCreatorOrderResult
{
    public string OrderId { get; set; } = string.Empty;
    public string? ApprovalUrl { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> CreatorMerchantIds { get; set; } = new();
}

/// <summary>
/// Result of capturing an order.
/// </summary>
public class CaptureResult
{
    public bool Success { get; set; }
    public string? CaptureId { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Represents an item in an order.
/// </summary>
public class OrderItem
{
    public string Name { get; set; } = string.Empty;
    public decimal UnitAmount { get; set; }
    public int Quantity { get; set; } = 1;
    public string? Description { get; set; }
    public int? SongMetadataId { get; set; }
}
