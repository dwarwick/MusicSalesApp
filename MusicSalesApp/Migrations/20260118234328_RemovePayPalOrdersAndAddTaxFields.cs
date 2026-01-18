using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RemovePayPalOrdersAndAddTaxFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayPalOrders");

            // First, add the new columns to StreamPayouts
            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                table: "StreamPayouts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NetAmount",
                table: "StreamPayouts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WithholdingRate",
                table: "StreamPayouts",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WithheldAmount",
                table: "StreamPayouts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Migrate existing data: AmountPaid becomes GrossAmount, NetAmount = AmountPaid (no withholding on historical payouts)
            migrationBuilder.Sql(@"
                UPDATE StreamPayouts 
                SET GrossAmount = AmountPaid, 
                    NetAmount = AmountPaid, 
                    WithholdingRate = 0, 
                    WithheldAmount = 0
            ");

            // Now drop the old AmountPaid column
            migrationBuilder.DropColumn(
                name: "AmountPaid",
                table: "StreamPayouts");

            // Add tax fields to Creators table
            migrationBuilder.AddColumn<string>(
                name: "ClaimedTreatyArticle",
                table: "Creators",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerifiedAt",
                table: "Creators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxBanditsSubmissionId",
                table: "Creators",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TaxFormExpirationDate",
                table: "Creators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxResidencyCountry",
                table: "Creators",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaxResidencyType",
                table: "Creators",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TreatyCountry",
                table: "Creators",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WithholdingRate",
                table: "Creators",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add back AmountPaid column
            migrationBuilder.AddColumn<decimal>(
                name: "AmountPaid",
                table: "StreamPayouts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Restore data from GrossAmount
            migrationBuilder.Sql(@"
                UPDATE StreamPayouts 
                SET AmountPaid = GrossAmount
            ");

            migrationBuilder.DropColumn(
                name: "GrossAmount",
                table: "StreamPayouts");

            migrationBuilder.DropColumn(
                name: "NetAmount",
                table: "StreamPayouts");

            migrationBuilder.DropColumn(
                name: "WithholdingRate",
                table: "StreamPayouts");

            migrationBuilder.DropColumn(
                name: "WithheldAmount",
                table: "StreamPayouts");

            migrationBuilder.DropColumn(
                name: "ClaimedTreatyArticle",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TaxBanditsSubmissionId",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TaxFormExpirationDate",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TaxResidencyCountry",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TaxResidencyType",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "TreatyCountry",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "WithholdingRate",
                table: "Creators");

            migrationBuilder.CreateTable(
                name: "PayPalOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayPalOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayPalOrders_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayPalOrders_UserId",
                table: "PayPalOrders",
                column: "UserId");
        }
    }
}
