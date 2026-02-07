using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RevertW9SubmissionIdToTaxBanditsSubmissionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "W9SubmissionId",
                table: "Creators",
                newName: "TaxBanditsSubmissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TaxBanditsSubmissionId",
                table: "Creators",
                newName: "W9SubmissionId");
        }
    }
}
