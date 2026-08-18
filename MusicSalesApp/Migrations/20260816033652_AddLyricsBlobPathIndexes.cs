using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsBlobPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SongLyrics_LrcBlobPath",
                table: "SongLyrics",
                column: "LrcBlobPath");

            migrationBuilder.CreateIndex(
                name: "IX_SongLyrics_TimingsBlobPath",
                table: "SongLyrics",
                column: "TimingsBlobPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SongLyrics_LrcBlobPath",
                table: "SongLyrics");

            migrationBuilder.DropIndex(
                name: "IX_SongLyrics_TimingsBlobPath",
                table: "SongLyrics");
        }
    }
}
