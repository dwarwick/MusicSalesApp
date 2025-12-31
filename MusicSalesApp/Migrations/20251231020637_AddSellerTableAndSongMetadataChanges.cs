using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerTableAndSongMetadataChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "SongMetadata",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Ensure all existing songs are marked as active
            migrationBuilder.Sql("UPDATE SongMetadata SET IsActive = 1 WHERE IsActive = 0");

            migrationBuilder.AddColumn<int>(
                name: "SellerId",
                table: "SongMetadata",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Sellers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PayPalMerchantId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PayPalTrackingId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PayPalReferralUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OnboardingStatus = table.Column<int>(type: "int", nullable: false),
                    PaymentsReceivable = table.Column<bool>(type: "bit", nullable: false),
                    PrimaryEmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    CommissionRate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Bio = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OnboardedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sellers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sellers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 1,
                column: "ClaimValue",
                value: "ManageOwnSongs");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 2,
                column: "ClaimValue",
                value: "ManageSongs");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 3,
                column: "ClaimValue",
                value: "ManageUsers");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 4,
                column: "ClaimValue",
                value: "UploadFiles");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "ClaimValue", "RoleId" },
                values: new object[] { "UseHangfire", 1 });

            migrationBuilder.InsertData(
                table: "AspNetRoleClaims",
                columns: new[] { "Id", "ClaimType", "ClaimValue", "RoleId" },
                values: new object[,]
                {
                    { 6, "Permission", "ValidatedUser", 1 },
                    { 7, "Permission", "ValidatedUser", 2 }
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[] { 3, "c3d4e5f6-a7b8-6c7d-0e1f-2a3b4c5d6e7f", "Seller", "SELLER" });

            migrationBuilder.InsertData(
                table: "AspNetRoleClaims",
                columns: new[] { "Id", "ClaimType", "ClaimValue", "RoleId" },
                values: new object[,]
                {
                    { 8, "Permission", "ValidatedUser", 3 },
                    { 9, "Permission", "UploadFiles", 3 },
                    { 10, "Permission", "ManageOwnSongs", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_SellerId",
                table: "SongMetadata",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_PayPalMerchantId",
                table: "Sellers",
                column: "PayPalMerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_PayPalTrackingId",
                table: "Sellers",
                column: "PayPalTrackingId");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_UserId",
                table: "Sellers",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SongMetadata_Sellers_SellerId",
                table: "SongMetadata",
                column: "SellerId",
                principalTable: "Sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SongMetadata_Sellers_SellerId",
                table: "SongMetadata");

            migrationBuilder.DropTable(
                name: "Sellers");

            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_SellerId",
                table: "SongMetadata");

            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "SongMetadata");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 1,
                column: "ClaimValue",
                value: "ManageUsers");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 2,
                column: "ClaimValue",
                value: "UploadFiles");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 3,
                column: "ClaimValue",
                value: "UseHangfire");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 4,
                column: "ClaimValue",
                value: "ValidatedUser");

            migrationBuilder.UpdateData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "ClaimValue", "RoleId" },
                values: new object[] { "ValidatedUser", 2 });
        }
    }
}
