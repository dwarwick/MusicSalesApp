using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCommissionRateFromCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommissionRate",
                table: "Creators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CommissionRate",
                table: "Creators",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0.15m);
        }
    }
}
