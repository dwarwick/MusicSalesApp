using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class FixMissingStreamPayoutColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These columns were defined in 20260103192952_AddStreamPayoutTracking but are
            // missing from the production database. Add them conditionally so the migration
            // is safe to run on databases where they already exist (e.g. local dev).
            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'GrossAmount') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [GrossAmount] decimal(18,2) NOT NULL DEFAULT 0;
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'NetAmount') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [NetAmount] decimal(18,2) NOT NULL DEFAULT 0;
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'WithheldAmount') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [WithheldAmount] decimal(18,2) NOT NULL DEFAULT 0;
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('StreamPayouts', 'WithholdingRate') IS NULL
                BEGIN
                    ALTER TABLE [StreamPayouts] ADD [WithholdingRate] decimal(5,4) NOT NULL DEFAULT 0;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is intentionally empty — we do not drop columns that were originally
            // defined in AddStreamPayoutTracking. Rolling back this migration should not
            // remove columns that other migrations also expect to exist.
        }
    }
}
