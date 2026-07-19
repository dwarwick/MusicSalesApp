using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddManageAllCreatorSongsPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scaffolding also emitted UpdateData ops renumbering every other seeded
            // AspNetRoleClaims row, because inserting this permission alphabetically
            // ahead of the existing admin claims shifts their diff-generated surrogate
            // Ids. Those rows' ClaimType/ClaimValue/RoleId don't actually change, so
            // only the genuinely new row below is kept (see AddSellerTableAndSongMetadataChanges
            // and AddHangfirePermission for the same precedent).
            migrationBuilder.InsertData(
                table: "AspNetRoleClaims",
                columns: new[] { "Id", "ClaimType", "ClaimValue", "RoleId" },
                values: new object[] { 10, "Permission", "ManageAllCreatorSongs", 1 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AspNetRoleClaims",
                keyColumn: "Id",
                keyValue: 10);
        }
    }
}
