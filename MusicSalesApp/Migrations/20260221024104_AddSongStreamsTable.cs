using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSongStreamsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SongStreams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<int>(type: "int", nullable: true),
                    StreamerUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongStreams_AspNetUsers_StreamerUserId",
                        column: x => x.StreamerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SongStreams_Creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Creators",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SongStreams_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongStreams_CreatedDate",
                table: "SongStreams",
                column: "CreatedDate");

            migrationBuilder.CreateIndex(
                name: "IX_SongStreams_CreatorId",
                table: "SongStreams",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_SongStreams_SongMetadataId",
                table: "SongStreams",
                column: "SongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_SongStreams_StreamerUserId",
                table: "SongStreams",
                column: "StreamerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongStreams");
        }
    }
}
