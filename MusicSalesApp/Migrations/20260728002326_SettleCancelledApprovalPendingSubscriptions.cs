using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class SettleCancelledApprovalPendingSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only. Cancelling an unapproved PayPal checkout wrote CANCELLED locally, but any
            // status refresh arriving inside PayPal's propagation window - the Manage Account page
            // performs one immediately after - reconciled the row straight back to APPROVAL_PENDING.
            // Nothing corrected it afterwards, because PayPal sends no lifecycle webhook for an
            // agreement the buyer never approved, so these rows are stranded permanently.
            //
            // Status APPROVAL_PENDING with a non-null CancelledAt is unreachable by any legitimate
            // path, which makes it a safe and self-limiting predicate: a checkout still genuinely in
            // flight has CancelledAt NULL and is left alone for the cleanup sweep to verify against
            // PayPal. ReconcilePayPalSubscriptionAsync now refuses the downgrade, so this cannot
            // recur.
            migrationBuilder.Sql("""
                UPDATE Subscriptions
                SET Status = 'CANCELLED'
                WHERE Status = 'APPROVAL_PENDING'
                  AND CancelledAt IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: restoring APPROVAL_PENDING would recreate the stranded state.
        }
    }
}
