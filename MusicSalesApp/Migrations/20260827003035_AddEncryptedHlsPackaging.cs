using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedHlsPackaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudioContentVersion",
                table: "SongMetadata",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HlsIv",
                table: "SongMetadata",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HlsKeyProtected",
                table: "SongMetadata",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HlsPackagedAt",
                table: "SongMetadata",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HlsSegmentCount",
                table: "SongMetadata",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HlsStreamId",
                table: "SongMetadata",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HlsTargetDurationSeconds",
                table: "SongMetadata",
                type: "float",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_HlsStreamId",
                table: "SongMetadata",
                column: "HlsStreamId",
                unique: true,
                filter: "[HlsStreamId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_Mp3BlobPath",
                table: "SongMetadata",
                column: "Mp3BlobPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_HlsStreamId",
                table: "SongMetadata");

            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_Mp3BlobPath",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "AudioContentVersion",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsIv",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsKeyProtected",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsPackagedAt",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsSegmentCount",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsStreamId",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "HlsTargetDurationSeconds",
                table: "SongMetadata");
        }
    }
}
