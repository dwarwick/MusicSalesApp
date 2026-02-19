using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceRawResponseWithBackupWithholding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RawResponse",
                table: "W9Requests");

            migrationBuilder.AddColumn<bool>(
                name: "SubjectToBackupWithholding",
                table: "W9Requests",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubjectToBackupWithholding",
                table: "W9Requests");

            migrationBuilder.AddColumn<string>(
                name: "RawResponse",
                table: "W9Requests",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
