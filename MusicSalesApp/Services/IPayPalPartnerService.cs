#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for PayPal Partner Referrals API operations.
/// Used for onboarding sellers to the platform.
/// </summary>
public interface IPayPalPartnerService
{
    /// <summary>
    /// Creates a partner referral (onboarding link) for a user to become a seller.
    /// </summary>
    /// <param name="userId">The ID of the user to onboard.</param>
    /// <param name="email">The email address of the user.</param>
    /// <returns>The referral URL for the user to complete PayPal onboarding, or null if failed.</returns>
    Task<PartnerReferralResult?> CreatePartnerReferralAsync(int userId, string email);

    /// <summary>
    /// Gets the merchant onboarding status from PayPal to check if onboarding is complete.
    /// </summary>
    /// <param name="partnerId">The partner merchant ID (platform's merchant ID).</param>
    /// <param name="merchantId">The merchant ID of the seller being onboarded.</param>
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
    /// Creates a multi-party order where the seller is the merchant of record.
    /// Platform commission is split using PayPal's payment platform partner fee.
    /// </summary>
    /// <param name="seller">The seller who is the merchant of record.</param>
    /// <param name="items">The items being purchased.</param>
    /// <param name="totalAmount">The total amount of the order.</param>
    /// <param name="platformFee">The platform fee (commission) amount.</param>
    /// <returns>The created order details.</returns>
    Task<MultiPartyOrderResult?> CreateMultiPartyOrderAsync(Seller seller, IEnumerable<OrderItem> items, decimal totalAmount, decimal platformFee);

    /// <summary>
    /// Creates a multi-seller order with multiple purchase units (one per seller).
    /// Supports up to 10 sellers per transaction. Each seller is the merchant of record for their items.
    /// </summary>
    /// <param name="sellerOrders">Dictionary mapping seller to their items and amounts.</param>
    /// <returns>The created order details with all seller merchant IDs.</returns>
    Task<MultiSellerOrderResult?> CreateMultiSellerOrderAsync(Dictionary<Seller, (IEnumerable<OrderItem> Items, decimal Amount, decimal PlatformFee)> sellerOrders);

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
/// OAuth integration status for a seller.
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
/// Result of creating a multi-seller order with multiple purchase units.
/// </summary>
public class MultiSellerOrderResult
{
    public string OrderId { get; set; } = string.Empty;
    public string? ApprovalUrl { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> SellerMerchantIds { get; set; } = new();
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
