using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSubscriptionPriceSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Id", "Description", "Key", "UpdatedAt", "Value" },
                values: new object[] { 1, "Monthly subscription price in USD", "SubscriptionPrice", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "3.99" });
        }
    }
}
