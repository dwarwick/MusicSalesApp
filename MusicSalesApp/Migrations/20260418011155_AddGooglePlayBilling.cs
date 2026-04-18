using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddGooglePlayBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingSource",
                table: "Subscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GooglePlayOrderId",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GooglePlayPurchaseToken",
                table: "Subscriptions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillingSource",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "GooglePlayOrderId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "GooglePlayPurchaseToken",
                table: "Subscriptions");
        }
    }
}
