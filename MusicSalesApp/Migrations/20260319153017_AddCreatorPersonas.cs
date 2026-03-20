using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PersonaId",
                table: "SongMetadata",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreatorPersonas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatorId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Bio = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImageBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WebsiteUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ImageWidth = table.Column<int>(type: "int", nullable: true),
                    ImageHeight = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorPersonas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreatorPersonas_Creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_PersonaId",
                table: "SongMetadata",
                column: "PersonaId");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorPersonas_CreatorId",
                table: "CreatorPersonas",
                column: "CreatorId");

            migrationBuilder.AddForeignKey(
                name: "FK_SongMetadata_CreatorPersonas_PersonaId",
                table: "SongMetadata",
                column: "PersonaId",
                principalTable: "CreatorPersonas",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SongMetadata_CreatorPersonas_PersonaId",
                table: "SongMetadata");

            migrationBuilder.DropTable(
                name: "CreatorPersonas");

            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_PersonaId",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "PersonaId",
                table: "SongMetadata");
        }
    }
}
