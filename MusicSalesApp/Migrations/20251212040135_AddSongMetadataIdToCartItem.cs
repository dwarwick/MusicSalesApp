using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSongMetadataIdToCartItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SongMetadataId",
                table: "CartItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CartItems_SongMetadataId",
                table: "CartItems",
                column: "SongMetadataId");

            migrationBuilder.AddForeignKey(
                name: "FK_CartItems_SongMetadata_SongMetadataId",
                table: "CartItems",
                column: "SongMetadataId",
                principalTable: "SongMetadata",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CartItems_SongMetadata_SongMetadataId",
                table: "CartItems");

            migrationBuilder.DropIndex(
                name: "IX_CartItems_SongMetadataId",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "SongMetadataId",
                table: "CartItems");
        }
    }
}
