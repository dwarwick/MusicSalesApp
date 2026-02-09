using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorLocationAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcknowledgmentAccepted",
                table: "Creators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgmentDateTimeUtc",
                table: "Creators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationCertification",
                table: "Creators",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgmentAccepted",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentDateTimeUtc",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "LocationCertification",
                table: "Creators");
        }
    }
}
