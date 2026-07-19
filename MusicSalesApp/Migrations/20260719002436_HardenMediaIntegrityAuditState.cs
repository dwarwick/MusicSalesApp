using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class HardenMediaIntegrityAuditState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveLockKey",
                table: "MediaIntegrityAuditRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOriginalSourceCheckInconclusive",
                table: "MediaIntegrityAuditItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MediaIntegrityAuditNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuditRunId = table.Column<int>(type: "int", nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Sent = table.Column<bool>(type: "bit", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIntegrityAuditNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaIntegrityAuditNotifications_MediaIntegrityAuditRuns_AuditRunId",
                        column: x => x.AuditRunId,
                        principalTable: "MediaIntegrityAuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaIntegrityAuditRuns_ActiveLockKey",
                table: "MediaIntegrityAuditRuns",
                column: "ActiveLockKey",
                unique: true,
                filter: "[ActiveLockKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaIntegrityAuditNotifications_AuditRunId_NotificationType_Recipient",
                table: "MediaIntegrityAuditNotifications",
                columns: new[] { "AuditRunId", "NotificationType", "Recipient" },
                unique: true,
                filter: "[NotificationType] IS NOT NULL AND [Recipient] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaIntegrityAuditNotifications");

            migrationBuilder.DropIndex(
                name: "IX_MediaIntegrityAuditRuns_ActiveLockKey",
                table: "MediaIntegrityAuditRuns");

            migrationBuilder.DropColumn(
                name: "ActiveLockKey",
                table: "MediaIntegrityAuditRuns");

            migrationBuilder.DropColumn(
                name: "IsOriginalSourceCheckInconclusive",
                table: "MediaIntegrityAuditItems");
        }
    }
}
