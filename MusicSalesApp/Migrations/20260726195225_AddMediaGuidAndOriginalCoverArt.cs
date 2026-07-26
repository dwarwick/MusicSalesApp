using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaGuidAndOriginalCoverArt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MediaGuid",
                table: "SongMetadata",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalCoverArtBlobPath",
                table: "SongMetadata",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalCoverArtFileName",
                table: "SongMetadata",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_MediaGuid",
                table: "SongMetadata",
                column: "MediaGuid",
                unique: true,
                filter: "[MediaGuid] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_MediaGuid",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "MediaGuid",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalCoverArtBlobPath",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalCoverArtFileName",
                table: "SongMetadata");
        }
    }
}
