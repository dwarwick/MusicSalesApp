using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <summary>
    /// Adds <c>StreamPayouts.TaxBanditsRetryCount</c> and <c>StreamPayouts.TaxBanditsSequenceId</c>,
    /// which together stop the hourly 1099 retry job looping on a batch it can never file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RetryPending1099TransactionsAsync groups pending payouts by PayPal batch id and used to emit
    /// one transaction per StreamPayout row. Since that batch id is also the SequenceId, a creator
    /// with N songs produced N transactions sharing one SequenceId, and TaxBandits rejected the
    /// whole request with "Duplicate Sequence Id exists. The Sequence Id is repeated more than
    /// once." A failed retry writes the status back to Pending, so the job re-sent the same doomed
    /// batch every hour and emailed the admin every time.
    /// </para>
    /// <para>
    /// TaxBanditsRetryCount caps that at MaxTaxBanditsRetryAttempts. TaxBanditsSequenceId records
    /// the key actually sent, so a retry reuses it verbatim instead of re-deriving it — a derived
    /// key that drifted would look like a new transaction to TaxBandits and double-report income.
    /// Existing rows start at 0 / NULL, which is correct: they get a fresh allowance of attempts and
    /// fall back to their PayPal batch id, which is what any earlier submission would have used.
    /// </para>
    /// <para>
    /// Both statements are guarded on COL_LENGTH rather than emitted as plain AddColumn calls.
    /// Production schema has drifted from this migration history before (see
    /// 20260903002902_DropOrphanedStreamPayoutAmountPaid), so a column may already exist on some
    /// environment; an unguarded ALTER would fail the deploy.
    /// </para>
    /// </remarks>
    public partial class AddStreamPayoutTaxBanditsRetryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'TaxBanditsRetryCount') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [TaxBanditsRetryCount] int NOT NULL
                        CONSTRAINT [DF_StreamPayouts_TaxBanditsRetryCount] DEFAULT 0;
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'TaxBanditsSequenceId') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [TaxBanditsSequenceId] nvarchar(100) NULL;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty, matching 20260903002902_DropOrphanedStreamPayoutAmountPaid.
            // Dropping these columns would restore the unbounded retry loop this migration exists to
            // stop, and would discard the record of which SequenceId was sent to TaxBandits.
        }
    }
}
