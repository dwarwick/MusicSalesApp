using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiveNewSongEmailsToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveNewSongEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 1,
                column: "ReceiveNewSongEmails",
                value: false);

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 2,
                column: "ReceiveNewSongEmails",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiveNewSongEmails",
                table: "AspNetUsers");
        }
    }
}
