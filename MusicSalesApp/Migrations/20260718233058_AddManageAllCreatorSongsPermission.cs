using Microsoft.EntityFrameworkCore.Migrations;
using MusicSalesApp.Common.Helpers;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddManageAllCreatorSongsPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AspNetRoleClaims.Id is an identity surrogate and production databases can
            // legitimately have different numeric IDs. Match the permission by its
            // logical identity and let SQL Server allocate an unused primary key.
            migrationBuilder.Sql(
                $"""
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [AspNetRoleClaims]
                    WHERE [RoleId] = 1
                      AND [ClaimType] = N'{CustomClaimTypes.Permission}'
                      AND [ClaimValue] = N'{Permissions.ManageAllCreatorSongs}'
                )
                BEGIN
                    INSERT INTO [AspNetRoleClaims] ([RoleId], [ClaimType], [ClaimValue])
                    VALUES (1, N'{CustomClaimTypes.Permission}', N'{Permissions.ManageAllCreatorSongs}');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                DELETE FROM [AspNetRoleClaims]
                WHERE [RoleId] = 1
                  AND [ClaimType] = N'{CustomClaimTypes.Permission}'
                  AND [ClaimValue] = N'{Permissions.ManageAllCreatorSongs}';
                """);
        }
    }
}
