using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTreatyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedTreatyArticle",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TreatyCountry",
                table: "Creators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedTreatyArticle",
                table: "Creators",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreatyCountry",
                table: "Creators",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);
        }
    }
}
