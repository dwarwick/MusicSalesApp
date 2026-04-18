using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddReportedSongsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportedSongs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    ReportingUserId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolutionDateTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionAccepted = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportedSongs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportedSongs_AspNetUsers_ReportingUserId",
                        column: x => x.ReportingUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ReportedSongs_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportedSongs_ReportingUserId",
                table: "ReportedSongs",
                column: "ReportingUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportedSongs_SongMetadataId",
                table: "ReportedSongs",
                column: "SongMetadataId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportedSongs");
        }
    }
}
