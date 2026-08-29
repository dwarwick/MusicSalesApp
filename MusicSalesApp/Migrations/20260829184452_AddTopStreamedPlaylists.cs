using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTopStreamedPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TopStreamedPlaylistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Window = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    StreamCount = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopStreamedPlaylistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopStreamedPlaylistEntries_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongStreams_CreatedDate_SongMetadataId",
                table: "SongStreams",
                columns: new[] { "CreatedDate", "SongMetadataId" });

            migrationBuilder.CreateIndex(
                name: "IX_TopStreamedPlaylistEntries_SongMetadataId",
                table: "TopStreamedPlaylistEntries",
                column: "SongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_TopStreamedPlaylistEntries_Window_DisplayOrder",
                table: "TopStreamedPlaylistEntries",
                columns: new[] { "Window", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TopStreamedPlaylistEntries");

            migrationBuilder.DropIndex(
                name: "IX_SongStreams_CreatedDate_SongMetadataId",
                table: "SongStreams");
        }
    }
}
