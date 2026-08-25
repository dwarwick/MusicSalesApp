using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadJobPublishMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Genre",
                table: "SongUploadJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAiGenerated",
                table: "SongUploadJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAiLyrics",
                table: "SongUploadJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAiVocals",
                table: "SongUploadJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PersonaId",
                table: "SongUploadJobs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Genre",
                table: "SongUploadJobs");

            migrationBuilder.DropColumn(
                name: "IsAiGenerated",
                table: "SongUploadJobs");

            migrationBuilder.DropColumn(
                name: "IsAiLyrics",
                table: "SongUploadJobs");

            migrationBuilder.DropColumn(
                name: "IsAiVocals",
                table: "SongUploadJobs");

            migrationBuilder.DropColumn(
                name: "PersonaId",
                table: "SongUploadJobs");
        }
    }
}
