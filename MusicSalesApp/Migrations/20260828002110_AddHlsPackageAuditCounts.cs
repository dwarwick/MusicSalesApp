using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHlsPackageAuditCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HlsPackageCheckInconclusive",
                table: "MediaIntegrityAuditRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HlsPackagesCheckedCount",
                table: "MediaIntegrityAuditRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HlsPackagesMissingCount",
                table: "MediaIntegrityAuditRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HlsPackageCheckInconclusive",
                table: "MediaIntegrityAuditRuns");

            migrationBuilder.DropColumn(
                name: "HlsPackagesCheckedCount",
                table: "MediaIntegrityAuditRuns");

            migrationBuilder.DropColumn(
                name: "HlsPackagesMissingCount",
                table: "MediaIntegrityAuditRuns");
        }
    }
}
