using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RequireSubscriptionBillingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Label anything still unlabelled BEFORE the column is tightened.
            //
            // The scaffolded version of this migration passed defaultValue: "" instead, which is
            // the wrong cure for this particular illness. The hazard is not the NULL itself, it is
            // that GetCurrentSubscriptionFromOtherProviderAsync asks for rows whose provider
            // differs from a given one - and an empty string differs from every provider just as a
            // NULL does. The row would still read as its own competitor, and reconciliation would
            // still cancel the agreement at the provider as a redundant overlap. Swapping NULL for
            // "" would have preserved the bug and hidden it behind a NOT NULL constraint.
            //
            // So each row is labelled from the evidence it actually carries. Anything with no
            // provider evidence at all predates the column, and only PayPal existed then.
            //
            // Empty strings are swept up too, in case a database somewhere already took the
            // scaffolded default. Literals rather than the BillingSources constants: a migration is
            // a frozen historical statement and must keep meaning what it meant if one is renamed.
            migrationBuilder.Sql("""
                UPDATE [Subscriptions]
                SET    [BillingSource] =
                       CASE
                           WHEN [PayPalSubscriptionId] IS NOT NULL THEN 'PayPal'
                           WHEN [GooglePlayPurchaseToken] IS NOT NULL THEN 'GooglePlay'
                           WHEN [AppStoreOriginalTransactionId] IS NOT NULL THEN 'Apple'
                           ELSE 'PayPal'
                       END
                WHERE  [BillingSource] IS NULL
                   OR  LTRIM(RTRIM([BillingSource])) = '';
                """);

            // No defaultValue: by this point there is nothing left for it to apply to, and leaving
            // it in would quietly reintroduce "" as an accepted value for future rows.
            migrationBuilder.AlterColumn<string>(
                name: "BillingSource",
                table: "Subscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BillingSource",
                table: "Subscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }
    }
}
