using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class ResolvePendingPayPalMismatchEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only. Mismatch detection used to treat APPROVAL_PENDING - an agreement the buyer
            // never approved and PayPal therefore cannot charge - as a billing mismatch, so ordinary
            // abandoned checkouts opened anomaly episodes and emailed the user and admin. The gate is
            // now narrowed to ACTIVE/SUSPENDED; close the episodes that gate already opened.
            //
            // Only the anomaly rows are touched. The subscriptions themselves must be verified
            // against PayPal before being closed out, which the cleanup-stale-paypal-checkouts job
            // does on its next run.
            migrationBuilder.Sql("""
                UPDATE a
                SET a.ResolvedAtUtc = SYSUTCDATETIME(),
                    a.NotificationClaimId = NULL,
                    a.NotificationClaimedAtUtc = NULL
                FROM PayPalSubscriptionAnomalies a
                INNER JOIN Subscriptions s ON s.Id = a.SubscriptionId
                WHERE a.ResolvedAtUtc IS NULL
                  AND s.Status = 'APPROVAL_PENDING';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: re-opening resolved episodes would re-send the notifications
            // this migration exists to stop.
        }
    }
}
