using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionTrialFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GooglePlayAutoRenewEnabled",
                table: "Subscriptions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialActivationEmailSentAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrialBasePlanId",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialConversionEmailSentAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialConvertedAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndDate",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrialOfferId",
                table: "Subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrialOfferTags",
                table: "Subscriptions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialStartDate",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GooglePlayAutoRenewEnabled",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialActivationEmailSentAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialBasePlanId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialConversionEmailSentAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialConvertedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialEndDate",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialOfferId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialOfferTags",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialStartDate",
                table: "Subscriptions");
        }
    }
}
