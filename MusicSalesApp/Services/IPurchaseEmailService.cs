using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending purchase confirmation emails.
/// </summary>
public interface IPurchaseEmailService
{
    /// <summary>
    /// Sends a purchase confirmation email for individual songs or albums.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="streamTunesOrderId">The internal StreamTunes order ID.</param>
    /// <param name="payPalOrderId">The PayPal order ID.</param>
    /// <param name="purchasedItems">The list of cart items that were purchased.</param>
    /// <param name="totalAmount">The total amount paid.</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendSongPurchaseConfirmationAsync(
        string userEmail,
        string userName,
        string streamTunesOrderId,
        string payPalOrderId,
        IEnumerable<CartItemWithMetadata> purchasedItems,
        decimal totalAmount,
        string baseUrl);

    /// <summary>
    /// Sends a subscription purchase confirmation email.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="subscription">The subscription details.</param>
    /// <param name="payPalSubscriptionId">The PayPal subscription ID.</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendSubscriptionConfirmationAsync(
        string userEmail,
        string userName,
        Subscription subscription,
        string payPalSubscriptionId,
        string baseUrl);
}

/// <summary>
/// Represents a cart item with its associated song metadata for email generation.
/// </summary>
public class CartItemWithMetadata
{
    /// <summary>
    /// The song file name.
    /// </summary>
    public string SongFileName { get; set; }

    /// <summary>
    /// The price of the item.
    /// </summary>
    public decimal Price { get; set; }
    
    /// <summary>
    /// When the item was added to the cart.
    /// </summary>
    public DateTime AddedAt { get; set; }

    /// <summary>
    /// The song metadata (may be null if not found).
    /// </summary>
    public SongMetadata SongMetadata { get; set; }

    /// <summary>
    /// The album name (if part of an album).
    /// </summary>
    public string AlbumName => SongMetadata?.AlbumName;

    /// <summary>
    /// Whether this is an album purchase vs individual song.
    /// </summary>
    public bool IsAlbumTrack => !string.IsNullOrEmpty(AlbumName);

    /// <summary>
    /// The image blob path for this song.
    /// </summary>
    public string ImageBlobPath => SongMetadata?.ImageBlobPath;
}
