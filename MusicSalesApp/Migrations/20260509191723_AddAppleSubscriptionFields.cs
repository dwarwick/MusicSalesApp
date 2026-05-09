using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAppleSubscriptionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppStoreAppAccountToken",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreEnvironment",
                table: "Subscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreOriginalTransactionId",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreProductId",
                table: "Subscriptions",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreTransactionId",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppStoreAppAccountToken",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AppStoreEnvironment",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AppStoreOriginalTransactionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AppStoreProductId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AppStoreTransactionId",
                table: "Subscriptions");
        }
    }
}
