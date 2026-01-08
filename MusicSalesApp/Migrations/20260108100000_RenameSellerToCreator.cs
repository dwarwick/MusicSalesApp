using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class RenameSellerToCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename Sellers table to Creators
            migrationBuilder.RenameTable(
                name: "Sellers",
                newName: "Creators");

            // Rename SellerId column in SongMetadata to CreatorId
            migrationBuilder.RenameColumn(
                name: "SellerId",
                table: "SongMetadata",
                newName: "CreatorId");

            // Update indexes for the renamed column
            migrationBuilder.RenameIndex(
                name: "IX_SongMetadata_SellerId",
                table: "SongMetadata",
                newName: "IX_SongMetadata_CreatorId");

            // Update indexes on Creators table (previously Sellers)
            migrationBuilder.RenameIndex(
                name: "IX_Sellers_UserId",
                table: "Creators",
                newName: "IX_Creators_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_Sellers_PayPalMerchantId",
                table: "Creators",
                newName: "IX_Creators_PayPalMerchantId");

            migrationBuilder.RenameIndex(
                name: "IX_Sellers_PayPalTrackingId",
                table: "Creators",
                newName: "IX_Creators_PayPalTrackingId");

            // Update the Seller role name to Creator in AspNetRoles table
            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Name",
                keyValue: "Seller",
                column: "Name",
                value: "Creator");

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "NormalizedName",
                keyValue: "SELLER",
                column: "NormalizedName",
                value: "CREATOR");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rename Creators table back to Sellers
            migrationBuilder.RenameTable(
                name: "Creators",
                newName: "Sellers");

            // Rename CreatorId column back to SellerId
            migrationBuilder.RenameColumn(
                name: "CreatorId",
                table: "SongMetadata",
                newName: "SellerId");

            // Revert indexes for the column
            migrationBuilder.RenameIndex(
                name: "IX_SongMetadata_CreatorId",
                table: "SongMetadata",
                newName: "IX_SongMetadata_SellerId");

            // Revert indexes on Sellers table (previously Creators)
            migrationBuilder.RenameIndex(
                name: "IX_Creators_UserId",
                table: "Sellers",
                newName: "IX_Sellers_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_Creators_PayPalMerchantId",
                table: "Sellers",
                newName: "IX_Sellers_PayPalMerchantId");

            migrationBuilder.RenameIndex(
                name: "IX_Creators_PayPalTrackingId",
                table: "Sellers",
                newName: "IX_Sellers_PayPalTrackingId");

            // Update the Creator role name back to Seller
            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Name",
                keyValue: "Creator",
                column: "Name",
                value: "Seller");

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "NormalizedName",
                keyValue: "CREATOR",
                column: "NormalizedName",
                value: "SELLER");
        }
    }
}
