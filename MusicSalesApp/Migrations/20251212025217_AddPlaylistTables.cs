using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SongMetadataId",
                table: "OwnedSongs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlaylistName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Playlists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPlaylists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PlaylistId = table.Column<int>(type: "int", nullable: false),
                    OwnedSongId = table.Column<int>(type: "int", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPlaylists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPlaylists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserPlaylists_OwnedSongs_OwnedSongId",
                        column: x => x.OwnedSongId,
                        principalTable: "OwnedSongs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserPlaylists_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnedSongs_SongMetadataId",
                table: "OwnedSongs",
                column: "SongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_UserId",
                table: "Playlists",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlaylists_OwnedSongId",
                table: "UserPlaylists",
                column: "OwnedSongId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlaylists_PlaylistId",
                table: "UserPlaylists",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlaylists_UserId",
                table: "UserPlaylists",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OwnedSongs_SongMetadata_SongMetadataId",
                table: "OwnedSongs",
                column: "SongMetadataId",
                principalTable: "SongMetadata",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OwnedSongs_SongMetadata_SongMetadataId",
                table: "OwnedSongs");

            migrationBuilder.DropTable(
                name: "UserPlaylists");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_OwnedSongs_SongMetadataId",
                table: "OwnedSongs");

            migrationBuilder.DropColumn(
                name: "SongMetadataId",
                table: "OwnedSongs");
        }
    }
}
