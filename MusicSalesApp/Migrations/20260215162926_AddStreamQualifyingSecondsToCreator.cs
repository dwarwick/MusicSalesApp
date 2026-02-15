using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddStreamQualifyingSecondsToCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StreamQualifyingSeconds",
                table: "Creators",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Id", "Description", "Key", "UpdatedAt", "Value" },
                values: new object[] { 2, "Number of continuous seconds of playback that qualifies as a stream", "StreamQualifyingSeconds", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "30" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DropColumn(
                name: "StreamQualifyingSeconds",
                table: "Creators");
        }
    }
}
