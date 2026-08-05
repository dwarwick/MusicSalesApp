using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSongUploadJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SongUploadJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MediaGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatorId = table.Column<int>(type: "int", nullable: false),
                    SongTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AlbumName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    SourceExtension = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    SourceFileSize = table.Column<long>(type: "bigint", nullable: false),
                    SourceContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CoverArtBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CoverArtFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CoverArtExtension = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    PlaybackBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    SourceWasAlreadyMp3 = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    StepUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SongMetadataId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongUploadJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongUploadJobs_Creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongUploadJobs_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongUploadJobs_CreatorId_Status",
                table: "SongUploadJobs",
                columns: new[] { "CreatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SongUploadJobs_MediaGuid",
                table: "SongUploadJobs",
                column: "MediaGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SongUploadJobs_SongMetadataId",
                table: "SongUploadJobs",
                column: "SongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_SongUploadJobs_Status_StepUpdatedAt",
                table: "SongUploadJobs",
                columns: new[] { "Status", "StepUpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongUploadJobs");
        }
    }
}
