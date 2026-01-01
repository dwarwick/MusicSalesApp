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

            // Use SQL to insert role claims only if they don't already exist
            // This avoids primary key conflicts if the database already has these IDs
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE Id = 6)
                BEGIN
                    SET IDENTITY_INSERT [AspNetRoleClaims] ON;
                    INSERT INTO [AspNetRoleClaims] ([Id], [ClaimType], [ClaimValue], [RoleId])
                    VALUES (6, N'Permission', N'ValidatedUser', 1);
                    SET IDENTITY_INSERT [AspNetRoleClaims] OFF;
                END
                ELSE IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE RoleId = 1 AND ClaimType = N'Permission' AND ClaimValue = N'ValidatedUser')
                BEGIN
                    INSERT INTO [AspNetRoleClaims] ([ClaimType], [ClaimValue], [RoleId])
                    VALUES (N'Permission', N'ValidatedUser', 1);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE Id = 7)
                BEGIN
                    SET IDENTITY_INSERT [AspNetRoleClaims] ON;
                    INSERT INTO [AspNetRoleClaims] ([Id], [ClaimType], [ClaimValue], [RoleId])
                    VALUES (7, N'Permission', N'ValidatedUser', 2);
                    SET IDENTITY_INSERT [AspNetRoleClaims] OFF;
                END
                ELSE IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE RoleId = 2 AND ClaimType = N'Permission' AND ClaimValue = N'ValidatedUser')
                BEGIN
                    INSERT INTO [AspNetRoleClaims] ([ClaimType], [ClaimValue], [RoleId])
                    VALUES (N'Permission', N'ValidatedUser', 2);
                END
            ");

            // Insert Seller role if it doesn't exist
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Id = 3)
                BEGIN
                    SET IDENTITY_INSERT [AspNetRoles] ON;
                    INSERT INTO [AspNetRoles] ([Id], [ConcurrencyStamp], [Name], [NormalizedName])
                    VALUES (3, N'c3d4e5f6-a7b8-6c7d-0e1f-2a3b4c5d6e7f', N'Seller', N'SELLER');
                    SET IDENTITY_INSERT [AspNetRoles] OFF;
                END
                ELSE IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Name = N'Seller')
                BEGIN
                    INSERT INTO [AspNetRoles] ([ConcurrencyStamp], [Name], [NormalizedName])
                    VALUES (N'c3d4e5f6-a7b8-6c7d-0e1f-2a3b4c5d6e7f', N'Seller', N'SELLER');
                END
            ");

            // Get the Seller role ID (could be 3 or auto-generated)
            // Insert Seller role claims
            migrationBuilder.Sql(@"
                DECLARE @SellerRoleId INT;
                SELECT @SellerRoleId = Id FROM AspNetRoles WHERE Name = N'Seller';

                IF @SellerRoleId IS NOT NULL
                BEGIN
                    -- ValidatedUser permission for Seller role
                    IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE RoleId = @SellerRoleId AND ClaimType = N'Permission' AND ClaimValue = N'ValidatedUser')
                    BEGIN
                        INSERT INTO [AspNetRoleClaims] ([ClaimType], [ClaimValue], [RoleId])
                        VALUES (N'Permission', N'ValidatedUser', @SellerRoleId);
                    END

                    -- UploadFiles permission for Seller role
                    IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE RoleId = @SellerRoleId AND ClaimType = N'Permission' AND ClaimValue = N'UploadFiles')
                    BEGIN
                        INSERT INTO [AspNetRoleClaims] ([ClaimType], [ClaimValue], [RoleId])
                        VALUES (N'Permission', N'UploadFiles', @SellerRoleId);
                    END

                    -- ManageOwnSongs permission for Seller role
                    IF NOT EXISTS (SELECT 1 FROM AspNetRoleClaims WHERE RoleId = @SellerRoleId AND ClaimType = N'Permission' AND ClaimValue = N'ManageOwnSongs')
                    BEGIN
                        INSERT INTO [AspNetRoleClaims] ([ClaimType], [ClaimValue], [RoleId])
                        VALUES (N'Permission', N'ManageOwnSongs', @SellerRoleId);
                    END
                END
            ");

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

            // Remove Seller role claims using dynamic SQL to handle different IDs
            migrationBuilder.Sql(@"
                DECLARE @SellerRoleId INT;
                SELECT @SellerRoleId = Id FROM AspNetRoles WHERE Name = N'Seller';

                IF @SellerRoleId IS NOT NULL
                BEGIN
                    DELETE FROM [AspNetRoleClaims] WHERE RoleId = @SellerRoleId;
                END
            ");

            // Delete ValidatedUser claims added by this migration (for Admin and User roles)
            migrationBuilder.Sql(@"
                -- Only delete if these exact records exist
                DELETE FROM [AspNetRoleClaims] WHERE Id = 6 AND ClaimValue = N'ValidatedUser' AND RoleId = 1;
                DELETE FROM [AspNetRoleClaims] WHERE Id = 7 AND ClaimValue = N'ValidatedUser' AND RoleId = 2;
                -- Also clean up any ValidatedUser claims with auto-generated IDs
                DELETE FROM [AspNetRoleClaims] WHERE ClaimValue = N'ValidatedUser' AND RoleId = 1 AND Id > 5;
                DELETE FROM [AspNetRoleClaims] WHERE ClaimValue = N'ValidatedUser' AND RoleId = 2 AND Id > 5;
            ");

            // Remove Seller role
            migrationBuilder.Sql(@"
                DELETE FROM [AspNetRoles] WHERE Name = N'Seller';
            ");

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
