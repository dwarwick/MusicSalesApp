using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is true because ApplicationUser declares both true. EF scaffolds the
            // CLR default (false) rather than the property initializer, which would leave the
            // schema disagreeing with the model AND give every existing user the opposite of what
            // every new one gets. AddColumn with a defaultValue also backfills existing rows, so
            // no separate UPDATE is needed - see AddArtistFollowFeature, which learned the same
            // thing about the email flags.
            //
            // Note what this does NOT do: nobody starts receiving push because of this migration.
            // A push needs a registered device token, and there are none until a client registers
            // one. The flag only decides whether a device, once registered, is used.
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveArtistMessagePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveArtistReleasePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushSentDateUtc",
                table: "ArtistReleaseNotifications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushSentDateUtc",
                table: "ArtistFollowerMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushDeviceTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeactivationReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeviceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDeviceTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ReceiveArtistMessagePush", "ReceiveArtistReleasePush" },
                values: new object[] { true, true });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "ReceiveArtistMessagePush", "ReceiveArtistReleasePush" },
                values: new object[] { true, true });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleaseNotifications_PushSentDateUtc",
                table: "ArtistReleaseNotifications",
                column: "PushSentDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_PushSentDateUtc",
                table: "ArtistFollowerMessages",
                column: "PushSentDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeviceTokens_Token",
                table: "PushDeviceTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeviceTokens_UserId_IsActive",
                table: "PushDeviceTokens",
                columns: new[] { "UserId", "IsActive" });

            // Settle everything that already exists.
            //
            // The dispatcher reads a null PushSentDateUtc as "this still needs a push". Without
            // this, every notification and message created between the follow feature shipping and
            // push shipping becomes eligible the moment the first device registers - so a listener
            // who installs the update gets a burst of alerts about releases they already know
            // about, some of them weeks old. Stamping them says "the dispatcher is done with
            // these", which is true: push did not exist when they were created.
            //
            // EXEC(N'...') for the usual reason - the davidtest Web Deploy path batches the whole
            // migration into one transaction, and SQL Server parses a batch before running any of
            // it, so a bare UPDATE naming a column the AddColumn above it just created fails with
            // "Invalid column name" even though it applies fine via Migrate() at startup.
            migrationBuilder.Sql(
                "EXEC(N'UPDATE ArtistReleaseNotifications SET PushSentDateUtc = GETUTCDATE() " +
                "WHERE PushSentDateUtc IS NULL')");

            migrationBuilder.Sql(
                "EXEC(N'UPDATE ArtistFollowerMessages SET PushSentDateUtc = GETUTCDATE() " +
                "WHERE PushSentDateUtc IS NULL')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDeviceTokens");

            migrationBuilder.DropIndex(
                name: "IX_ArtistReleaseNotifications_PushSentDateUtc",
                table: "ArtistReleaseNotifications");

            migrationBuilder.DropIndex(
                name: "IX_ArtistFollowerMessages_PushSentDateUtc",
                table: "ArtistFollowerMessages");

            migrationBuilder.DropColumn(
                name: "ReceiveArtistMessagePush",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReceiveArtistReleasePush",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PushSentDateUtc",
                table: "ArtistReleaseNotifications");

            migrationBuilder.DropColumn(
                name: "PushSentDateUtc",
                table: "ArtistFollowerMessages");
        }
    }
}
