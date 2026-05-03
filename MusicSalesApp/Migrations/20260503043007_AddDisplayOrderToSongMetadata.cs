using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayOrderToSongMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.SongMetadata', 'DisplayOrder') IS NULL
BEGIN
    ALTER TABLE [dbo].[SongMetadata] ADD [DisplayOrder] int NULL;
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.SongMetadata', 'DisplayOrder') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[SongMetadata] DROP COLUMN [DisplayOrder];
END
");
        }
    }
}
