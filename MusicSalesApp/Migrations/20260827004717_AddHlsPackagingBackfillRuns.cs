using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHlsPackagingBackfillRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HlsPackagingBackfillRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ActiveLockKey = table.Column<int>(type: "int", nullable: true),
                    InitiatedByUserId = table.Column<int>(type: "int", nullable: true),
                    InitiatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HangfireJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCallbackAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalItemCount = table.Column<int>(type: "int", nullable: false),
                    DispatchedCount = table.Column<int>(type: "int", nullable: false),
                    SucceededCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsPackagingBackfillRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HlsPackagingBackfillFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsPackagingBackfillFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HlsPackagingBackfillFailures_HlsPackagingBackfillRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "HlsPackagingBackfillRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HlsPackagingBackfillFailures_RunId",
                table: "HlsPackagingBackfillFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_HlsPackagingBackfillRuns_ActiveLockKey",
                table: "HlsPackagingBackfillRuns",
                column: "ActiveLockKey",
                unique: true,
                filter: "[ActiveLockKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HlsPackagingBackfillRuns_CreatedAt",
                table: "HlsPackagingBackfillRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HlsPackagingBackfillFailures");

            migrationBuilder.DropTable(
                name: "HlsPackagingBackfillRuns");
        }
    }
}
