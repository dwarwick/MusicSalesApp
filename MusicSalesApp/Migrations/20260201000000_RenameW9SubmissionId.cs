#nullable disable
using Microsoft.EntityFrameworkCore.Migrations;

namespace MusicSalesApp.Migrations;

/// <summary>
/// Renames TaxBanditsSubmissionId to W9SubmissionId in the Creators table.
/// This reflects the migration from TaxBandits to Avalara/Track1099 for W-9 form processing.
/// </summary>
public partial class RenameW9SubmissionId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "TaxBanditsSubmissionId",
            table: "Creators",
            newName: "W9SubmissionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "W9SubmissionId",
            table: "Creators",
            newName: "TaxBanditsSubmissionId");
    }
}
