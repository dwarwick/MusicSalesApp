using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLegacyPayPalBillingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only. Subscriptions created before the BillingSource column existed carry NULL,
            // and a NULL provider is not merely untidy - it is actively dangerous.
            //
            // GetCurrentSubscriptionFromOtherProviderAsync asks for rows whose BillingSource differs
            // from a given provider, and EF gives that comparison C# null semantics: NULL differs
            // from everything, so such a row answers "yes, a different provider currently covers
            // this user" for every provider, including the one it belongs to. Reconciliation reads
            // that as an overlap and cancels the agreement at PayPal - a live, paying subscriber
            // cancelled because their own row was mistaken for a competing one.
            //
            // The same NULL also hid these rows from the nightly entitlement drift sweep, which
            // matches BillingSource = 'PayPal' exactly, so the oldest and most drifted rows in the
            // table were the only ones it never checked.
            //
            // Only rows carrying a PayPal agreement id are touched; nothing else can be inferred.
            // Literals rather than the BillingSources constants, as a migration is a frozen
            // historical statement and must keep meaning what it meant if a constant is renamed.
            migrationBuilder.Sql("""
                UPDATE [Subscriptions]
                SET    [BillingSource] = 'PayPal'
                WHERE  [BillingSource] IS NULL
                  AND  [PayPalSubscriptionId] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. Restoring NULL would reinstate the overlap hazard above, and
            // nothing distinguishes a row this backfilled from one that was always correct.
        }
    }
}
