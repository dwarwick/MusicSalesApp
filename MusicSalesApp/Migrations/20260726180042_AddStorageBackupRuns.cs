using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageBackupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageBackupRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    RestoreScope = table.Column<int>(type: "int", nullable: false),
                    OverwriteNewerLive = table.Column<bool>(type: "bit", nullable: false),
                    ForceFullCopy = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ActiveLockKey = table.Column<int>(type: "int", nullable: true),
                    InitiatedByUserId = table.Column<int>(type: "int", nullable: true),
                    InitiatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HangfireJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TriggerSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalBlobCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    CopiedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedNewerLiveCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CopiedBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageBackupRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageBackupContainerProgresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    SourceContainerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DestinationContainerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TotalBlobCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    CopiedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedNewerLiveCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CopiedBytes = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageBackupContainerProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageBackupContainerProgresses_StorageBackupRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "StorageBackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageBackupItemFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    ContainerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BlobName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Diagnostic = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageBackupItemFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageBackupItemFailures_StorageBackupRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "StorageBackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageBackupContainerProgresses_RunId_SourceContainerName",
                table: "StorageBackupContainerProgresses",
                columns: new[] { "RunId", "SourceContainerName" },
                unique: true,
                filter: "[SourceContainerName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBackupItemFailures_RunId",
                table: "StorageBackupItemFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBackupRuns_ActiveLockKey",
                table: "StorageBackupRuns",
                column: "ActiveLockKey",
                unique: true,
                filter: "[ActiveLockKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBackupRuns_CreatedAt",
                table: "StorageBackupRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageBackupContainerProgresses");

            migrationBuilder.DropTable(
                name: "StorageBackupItemFailures");

            migrationBuilder.DropTable(
                name: "StorageBackupRuns");
        }
    }
}
