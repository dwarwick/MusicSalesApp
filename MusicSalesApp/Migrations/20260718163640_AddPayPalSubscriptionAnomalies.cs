using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPayPalSubscriptionAnomalies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PayPalSubscriptionAnomalies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PayPalSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LocalStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderTrialEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderLastPaymentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderNextBillingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LocalTrialEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LocalLastPaymentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LocalNextBillingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LocalEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedPaymentsCount = table.Column<int>(type: "int", nullable: false),
                    ReconciliationError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserEmailSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdminEmailSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotificationClaimId = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    NotificationClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayPalSubscriptionAnomalies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayPalSubscriptionAnomalies_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayPalSubscriptionAnomalies_CorrelationId",
                table: "PayPalSubscriptionAnomalies",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayPalSubscriptionAnomalies_SubscriptionId",
                table: "PayPalSubscriptionAnomalies",
                column: "SubscriptionId",
                unique: true,
                filter: "[ResolvedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayPalSubscriptionAnomalies_UserId",
                table: "PayPalSubscriptionAnomalies",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayPalSubscriptionAnomalies");
        }
    }
}
