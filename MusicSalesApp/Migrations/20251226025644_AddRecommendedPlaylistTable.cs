using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendedPlaylistTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecommendedPlaylists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendedPlaylists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendedPlaylists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecommendedPlaylists_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedPlaylists_SongMetadataId",
                table: "RecommendedPlaylists",
                column: "SongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedPlaylists_UserId",
                table: "RecommendedPlaylists",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedPlaylists_UserId_GeneratedAt",
                table: "RecommendedPlaylists",
                columns: new[] { "UserId", "GeneratedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendedPlaylists");
        }
    }
}
