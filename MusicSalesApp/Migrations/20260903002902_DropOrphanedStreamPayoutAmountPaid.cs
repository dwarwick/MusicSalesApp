using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <summary>
    /// Removes the orphaned <c>StreamPayouts.AmountPaid</c> column from databases that still carry it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table was created with <c>AmountPaid decimal(18,2) NOT NULL</c> (no default) by
    /// 20260103192952_AddStreamPayoutTracking. Commit f4b988a later rewrote that migration in place to
    /// declare GrossAmount/NetAmount/WithheldAmount/WithholdingRate instead, and stripped the
    /// AddColumn + backfill + DropColumn("AmountPaid") sequence out of
    /// 20260118234328_RemovePayPalOrdersAndAddTaxFields. Production had already recorded both
    /// migrations as applied, so neither edit ever ran there: the column survives, EF does not map it,
    /// and every INSERT into StreamPayouts fails with "Cannot insert the value NULL into column
    /// 'AmountPaid'". That is what stopped the 2026-09-02 payout run from recording two payments PayPal
    /// had already sent.
    /// </para>
    /// <para>
    /// The backfill below is the one f4b988a deleted, and it must run before the column is dropped.
    /// 20260310224329_FixMissingStreamPayoutColumns added GrossAmount/NetAmount as NOT NULL DEFAULT 0
    /// to a production table that already had rows, so every payout written before 2026-03-12 has
    /// GrossAmount = 0 and NetAmount = 0 with the real figure living only in AmountPaid. Dropping the
    /// column first would destroy that history, which feeds the creator dashboard and 1099 reporting.
    /// WithheldAmount/WithholdingRate are correctly 0 for those rows — no withholding was applied
    /// before the tax work — which is what the original backfill set them to as well.
    /// </para>
    /// </remarks>
    public partial class DropOrphanedStreamPayoutAmountPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every statement naming AmountPaid is wrapped in EXEC so it is compiled when it runs
            // rather than when the batch is parsed. Two reasons, and both are load-bearing:
            //   1. On a database that never had the column (dev/test, and anything created from the
            //      current snapshot), SQL Server would fail the whole batch at parse time with
            //      "Invalid column name 'AmountPaid'" even though the IF guard is false.
            //   2. Web Deploy sends a migration as a single pre-compiled batch, so the same applies
            //      there — see the note in 20260901213802_AddStreamPayoutPaymentStatus.
            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'AmountPaid') IS NOT NULL
                BEGIN
                    -- Restore the pre-2026-03-12 payout history that FixMissingStreamPayoutColumns
                    -- defaulted to 0. Guarded on GrossAmount = 0 so rows written after that migration,
                    -- which already carry correct amounts, are left alone.
                    EXEC(N'
                        UPDATE [StreamPayouts]
                        SET [GrossAmount] = [AmountPaid],
                            [NetAmount]   = [AmountPaid]
                        WHERE [GrossAmount] = 0 AND [AmountPaid] > 0');

                    -- The column is NOT NULL with no default today, but a default constraint would
                    -- block the DROP COLUMN, so clear one if some environment picked it up.
                    DECLARE @defaultConstraint sysname;
                    SELECT @defaultConstraint = dc.name
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c
                        ON c.default_object_id = dc.object_id
                    WHERE dc.parent_object_id = OBJECT_ID('StreamPayouts')
                      AND c.name = 'AmountPaid';

                    IF @defaultConstraint IS NOT NULL
                    BEGIN
                        EXEC(N'ALTER TABLE [StreamPayouts] DROP CONSTRAINT [' + @defaultConstraint + ']');
                    END

                    EXEC(N'ALTER TABLE [StreamPayouts] DROP COLUMN [AmountPaid]');
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty, matching 20260310224329_FixMissingStreamPayoutColumns. Re-adding
            // AmountPaid would recreate the exact NOT NULL column that breaks every insert, and the
            // data it held has been folded into GrossAmount/NetAmount by Up.
        }
    }
}
