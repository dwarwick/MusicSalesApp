using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RemovePayPalPartnerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Creators_PayPalMerchantId",
                table: "Creators");

            migrationBuilder.DropIndex(
                name: "IX_Creators_PayPalTrackingId",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "PayPalMerchantId",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "PayPalReferralUrl",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "PayPalTrackingId",
                table: "Creators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayPalMerchantId",
                table: "Creators",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPalReferralUrl",
                table: "Creators",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPalTrackingId",
                table: "Creators",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Creators_PayPalMerchantId",
                table: "Creators",
                column: "PayPalMerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_Creators_PayPalTrackingId",
                table: "Creators",
                column: "PayPalTrackingId");
        }
    }
}
