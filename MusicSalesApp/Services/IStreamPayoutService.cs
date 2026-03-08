namespace MusicSalesApp.Services;

/// <summary>
/// Service for processing stream payouts to creators.
/// Creators are paid based on the number of streams their songs receive.
/// For US creators, no withholding is applied (unless subject to backup withholding).
/// For foreign creators, no withholding is applied.
/// </summary>
public interface IStreamPayoutService
{
    /// <summary>
    /// Calculates pending payouts for all creators and processes payments via PayPal.
    /// Minimum payout threshold is $5.00. Rate is $5 per 1000 streams (0.005 per stream).
    /// Applies withholding for US creators subject to backup withholding.
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
    /// <param name="totalTipAmount">Total tip amount included in this payout</param>
    /// <param name="tipWithheldAmount">Amount withheld from tips for tax purposes</param>
    /// <returns>True if email sent successfully</returns>
    Task<bool> SendPayoutReceiptEmailAsync(
        int creatorId,
        List<Models.StreamPayout> payoutRecords,
        decimal totalGrossAmount,
        decimal totalWithheldAmount,
        decimal totalNetAmount,
        string payPalTransactionId,
        decimal totalTipAmount = 0m,
        decimal tipWithheldAmount = 0m);

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

    /// <summary>
    /// Gets all stream payouts in the system for admin reporting.
    /// </summary>
    Task<List<Models.StreamPayout>> GetAllPayoutsAsync();

    /// <summary>
    /// Retries reporting pending 1099 transactions to TaxBandits.
    /// This processes StreamPayout records with TaxBanditsStatus of "Pending" that belong to US creators.
    /// </summary>
    /// <returns>The number of payout records successfully reported.</returns>
    Task<int> RetryPending1099TransactionsAsync();
}
