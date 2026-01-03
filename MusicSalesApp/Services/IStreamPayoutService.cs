namespace MusicSalesApp.Services;

/// <summary>
/// Service for processing stream payouts to sellers.
/// Sellers are paid based on the number of streams their songs receive.
/// </summary>
public interface IStreamPayoutService
{
    /// <summary>
    /// Calculates pending payouts for all sellers and processes payments via PayPal.
    /// Minimum payout threshold is $5.00. Rate is $5 per 1000 streams (0.005 per stream).
    /// </summary>
    /// <returns>The number of sellers that received payouts.</returns>
    Task<int> ProcessPendingPayoutsAsync();

    /// <summary>
    /// Sends a payout receipt email to a seller with itemized details of songs paid.
    /// </summary>
    /// <param name="sellerId">The seller ID</param>
    /// <param name="payoutRecords">List of payout records for this seller</param>
    /// <param name="totalAmount">Total amount paid</param>
    /// <param name="payPalTransactionId">PayPal transaction ID</param>
    /// <returns>True if email sent successfully</returns>
    Task<bool> SendPayoutReceiptEmailAsync(
        int sellerId,
        List<Models.StreamPayout> payoutRecords,
        decimal totalAmount,
        string payPalTransactionId);

    /// <summary>
    /// Gets the total unpaid earnings for a seller across all their songs.
    /// </summary>
    /// <param name="sellerId">The seller ID</param>
    /// <returns>Total unpaid earnings in USD</returns>
    Task<decimal> GetUnpaidEarningsAsync(int sellerId);

    /// <summary>
    /// Gets detailed payout history for a seller.
    /// </summary>
    /// <param name="sellerId">The seller ID</param>
    /// <returns>List of payout records</returns>
    Task<List<Models.StreamPayout>> GetPayoutHistoryAsync(int sellerId);
}
