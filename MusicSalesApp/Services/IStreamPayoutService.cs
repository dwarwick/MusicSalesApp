namespace MusicSalesApp.Services;

/// <summary>
/// Service for processing stream payouts to creators.
/// Creators are paid based on the number of streams their songs receive.
/// For US creators, no withholding is applied (unless subject to backup withholding).
/// For foreign creators, withholding is applied based on tax treaty rates or default 30%.
/// </summary>
public interface IStreamPayoutService
{
    /// <summary>
    /// Calculates pending payouts for all creators and processes payments via PayPal.
    /// Minimum payout threshold is $5.00. Rate is $5 per 1000 streams (0.005 per stream).
    /// Applies withholding for foreign creators based on their tax treaty status.
    /// </summary>
    /// <returns>The number of creators that received payouts.</returns>
    Task<int> ProcessPendingPayoutsAsync();

    /// <summary>
    /// Sends a payout receipt email to a creator with itemized details of songs paid.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="payoutRecords">List of payout records for this creator</param>
    /// <param name="totalGrossAmount">Total gross amount before withholding</param>
    /// <param name="totalWithheldAmount">Total amount withheld for taxes</param>
    /// <param name="totalNetAmount">Total net amount paid to creator</param>
    /// <param name="payPalTransactionId">PayPal transaction ID</param>
    /// <returns>True if email sent successfully</returns>
    Task<bool> SendPayoutReceiptEmailAsync(
        int creatorId,
        List<Models.StreamPayout> payoutRecords,
        decimal totalGrossAmount,
        decimal totalWithheldAmount,
        decimal totalNetAmount,
        string payPalTransactionId);

    /// <summary>
    /// Gets the total unpaid earnings for a creator across all their songs.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>Total unpaid earnings in USD</returns>
    Task<decimal> GetUnpaidEarningsAsync(int creatorId);

    /// <summary>
    /// Gets detailed payout history for a creator.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>List of payout records</returns>
    Task<List<Models.StreamPayout>> GetPayoutHistoryAsync(int creatorId);
}
