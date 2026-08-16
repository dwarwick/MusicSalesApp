using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsAlignmentAndDurableTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DurableFunctionTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstanceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FunctionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RuntimeStatusRaw = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FailureDetail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StatusQueryUri = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TerminateUri = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DurableFunctionTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SongLyrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    LyricsBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimingsBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LrcBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    AlignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongLyrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongLyrics_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LyricsAlignmentJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<int>(type: "int", nullable: false),
                    DurableFunctionTaskId = table.Column<int>(type: "int", nullable: true),
                    LyricsBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    StepUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LyricsAlignmentJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LyricsAlignmentJobs_DurableFunctionTasks_DurableFunctionTaskId",
                        column: x => x.DurableFunctionTaskId,
                        principalTable: "DurableFunctionTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LyricsAlignmentJobs_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DurableFunctionTasks_FunctionName_Status",
                table: "DurableFunctionTasks",
                columns: new[] { "FunctionName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DurableFunctionTasks_InstanceId",
                table: "DurableFunctionTasks",
                column: "InstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LyricsAlignmentJobs_CreatorId_Status",
                table: "LyricsAlignmentJobs",
                columns: new[] { "CreatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LyricsAlignmentJobs_DurableFunctionTaskId",
                table: "LyricsAlignmentJobs",
                column: "DurableFunctionTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_LyricsAlignmentJobs_JobId",
                table: "LyricsAlignmentJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LyricsAlignmentJobs_SongMetadataId_Status",
                table: "LyricsAlignmentJobs",
                columns: new[] { "SongMetadataId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LyricsAlignmentJobs_Status_StepUpdatedAt",
                table: "LyricsAlignmentJobs",
                columns: new[] { "Status", "StepUpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SongLyrics_SongMetadataId",
                table: "SongLyrics",
                column: "SongMetadataId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LyricsAlignmentJobs");

            migrationBuilder.DropTable(
                name: "SongLyrics");

            migrationBuilder.DropTable(
                name: "DurableFunctionTasks");
        }
    }
}
