using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPayPalOfferTermsAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PayPalOfferVersion",
                table: "Subscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionTermsAcceptedAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayPalOfferVersion",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "SubscriptionTermsAcceptedAt",
                table: "Subscriptions");
        }
    }
}
