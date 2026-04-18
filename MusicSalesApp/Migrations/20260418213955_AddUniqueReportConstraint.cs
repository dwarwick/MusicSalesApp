using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueReportConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportedSongs_SongMetadataId",
                table: "ReportedSongs");

            migrationBuilder.CreateIndex(
                name: "IX_ReportedSongs_SongMetadataId_ReportingUserId",
                table: "ReportedSongs",
                columns: new[] { "SongMetadataId", "ReportingUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportedSongs_SongMetadataId_ReportingUserId",
                table: "ReportedSongs");

            migrationBuilder.CreateIndex(
                name: "IX_ReportedSongs_SongMetadataId",
                table: "ReportedSongs",
                column: "SongMetadataId");
        }
    }
}
