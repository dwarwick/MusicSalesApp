using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorPayoutRequirementsAcknowledgment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PayoutRequirementsAcknowledged",
                table: "Creators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PayoutRequirementsAcknowledgedAtUtc",
                table: "Creators",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayoutRequirementsAcknowledged",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "PayoutRequirementsAcknowledgedAtUtc",
                table: "Creators");
        }
    }
}
