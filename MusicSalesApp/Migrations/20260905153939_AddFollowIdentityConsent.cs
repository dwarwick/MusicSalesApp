using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowIdentityConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RevealPersonaToFollowedArtists",
                table: "Creators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FollowAsPersonaId",
                table: "ArtistFollowers",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevealPersonaToFollowedArtists",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "FollowAsPersonaId",
                table: "ArtistFollowers");
        }
    }
}
