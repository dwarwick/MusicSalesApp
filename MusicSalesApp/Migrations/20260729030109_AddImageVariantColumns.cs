using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddImageVariantColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverArtVariantVersion",
                table: "SongMetadata",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CoverArtVariantWidths",
                table: "SongMetadata",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageVariantVersion",
                table: "CreatorPersonas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ImageVariantWidths",
                table: "CreatorPersonas",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverArtVariantVersion",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "CoverArtVariantWidths",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "ImageVariantVersion",
                table: "CreatorPersonas");

            migrationBuilder.DropColumn(
                name: "ImageVariantWidths",
                table: "CreatorPersonas");
        }
    }
}
