using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddImageVariantBackfillRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageVariantBackfillRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    RegenerateExisting = table.Column<bool>(type: "bit", nullable: false),
                    RemoveLegacyPngSharingImages = table.Column<bool>(type: "bit", nullable: false),
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
                    TotalItemCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    GeneratedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    VariantBlobCount = table.Column<int>(type: "int", nullable: false),
                    BytesWritten = table.Column<long>(type: "bigint", nullable: false),
                    UndersizedSourceCount = table.Column<int>(type: "int", nullable: false),
                    LegacySharingImagesRemoved = table.Column<int>(type: "int", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageVariantBackfillRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageVariantBackfillItemFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    ItemKind = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageVariantBackfillItemFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageVariantBackfillItemFailures_ImageVariantBackfillRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ImageVariantBackfillRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageVariantBackfillItemFailures_RunId",
                table: "ImageVariantBackfillItemFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageVariantBackfillRuns_ActiveLockKey",
                table: "ImageVariantBackfillRuns",
                column: "ActiveLockKey",
                unique: true,
                filter: "[ActiveLockKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImageVariantBackfillRuns_CreatedAt",
                table: "ImageVariantBackfillRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageVariantBackfillItemFailures");

            migrationBuilder.DropTable(
                name: "ImageVariantBackfillRuns");
        }
    }
}
