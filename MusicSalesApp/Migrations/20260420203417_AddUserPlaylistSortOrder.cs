using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPlaylistSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "UserPlaylists",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill SortOrder for existing rows so song order stays stable
            // when the service switches to ordering by SortOrder.
            //
            // The UPDATE is wrapped in EXEC(N'...') so SQL Server defers parsing
            // until execution time. Without this, the idempotent migration script
            // combines the ALTER TABLE and UPDATE into a single batch whose
            // compile-time parser would fail with "Invalid column name 'SortOrder'"
            // because the column does not yet exist when the batch is parsed.
            migrationBuilder.Sql(@"
                EXEC(N'
                    WITH NumberedPlaylists AS (
                        SELECT
                            Id,
                            ROW_NUMBER() OVER (PARTITION BY UserId, PlaylistId ORDER BY AddedAt, Id) AS NewSortOrder
                        FROM UserPlaylists
                    )
                    UPDATE up
                    SET up.SortOrder = np.NewSortOrder
                    FROM UserPlaylists up
                    INNER JOIN NumberedPlaylists np ON up.Id = np.Id;
                ');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "UserPlaylists");
        }
    }
}
