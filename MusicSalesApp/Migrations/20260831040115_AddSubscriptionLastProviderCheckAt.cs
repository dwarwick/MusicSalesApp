using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionLastProviderCheckAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastProviderCheckAtUtc",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_BillingSource_LastProviderCheckAtUtc_Id",
                table: "Subscriptions",
                columns: new[] { "BillingSource", "LastProviderCheckAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_BillingSource_LastProviderCheckAtUtc_Id",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "LastProviderCheckAtUtc",
                table: "Subscriptions");
        }
    }
}
