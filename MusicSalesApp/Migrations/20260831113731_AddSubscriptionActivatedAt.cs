using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionActivatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivatedAtUtc",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            // Backfill, so the column change is a no-op for everyone who already exists.
            //
            // HasPriorActivatedSubscriptionAsync stops reading "Status is currently ACTIVE or
            // SUSPENDED" and starts reading this column. Without the backfill every current
            // subscriber whose only proof of activation was their status would be offered another
            // free trial the moment their subscription ended.
            //
            // The WHERE therefore reproduces the old predicate exactly - every clause that used to
            // mean "this user has subscribed before" - so the new answer equals the old one for all
            // existing rows. The value is the earliest defensible activation moment; StartDate is
            // the fallback for a row that carries no dated evidence at all.
            //
            // Literals rather than the SubscriptionStatuses/BillingSources constants on purpose: a
            // migration is a frozen historical statement, and it must keep meaning what it meant if
            // one of those constants is ever renamed.
            migrationBuilder.Sql("""
                UPDATE [Subscriptions]
                SET    [ActivatedAtUtc] = COALESCE([TrialStartDate], [LastPaymentDate], [StartDate])
                WHERE  [ActivatedAtUtc] IS NULL
                  AND  ([LastPaymentDate] IS NOT NULL
                     OR [TrialStartDate] IS NOT NULL
                     OR [TrialEndDate] IS NOT NULL
                     OR [TrialConvertedAt] IS NOT NULL
                     OR [Status] IN ('ACTIVE', 'SUSPENDED')
                     OR ([BillingSource] = 'GooglePlay' AND [GooglePlayPurchaseToken] IS NOT NULL)
                     OR ([BillingSource] = 'Apple' AND [AppStoreOriginalTransactionId] IS NOT NULL));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivatedAtUtc",
                table: "Subscriptions");
        }
    }
}
