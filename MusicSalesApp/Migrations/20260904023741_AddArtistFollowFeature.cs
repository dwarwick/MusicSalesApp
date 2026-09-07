using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistFollowFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstPublishedAtUtc",
                table: "SongMetadata",
                type: "datetime2",
                nullable: true);

            // defaultValue is true because ApplicationUser declares these true. EF scaffolds the
            // CLR default (false) rather than the property initializer, which would have left the
            // schema disagreeing with the model AND given every existing user the opposite of what
            // every new user gets - see the backfill at the end of this method.
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveArtistMessageEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveArtistReleaseEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ArtistFollowers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatorPersonaId = table.Column<int>(type: "int", nullable: false),
                    ListenerUserId = table.Column<int>(type: "int", nullable: false),
                    FollowedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceSongMetadataId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UnfollowedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleaseNotificationsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ArtistMessagesEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsBlockedByListener = table.Column<bool>(type: "bit", nullable: false),
                    BlockedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnonymousListenerNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistFollowers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistFollowers_AspNetUsers_ListenerUserId",
                        column: x => x.ListenerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ArtistFollowers_CreatorPersonas_CreatorPersonaId",
                        column: x => x.CreatorPersonaId,
                        principalTable: "CreatorPersonas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistFollowers_SongMetadata_SourceSongMetadataId",
                        column: x => x.SourceSongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ArtistReleaseNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatorPersonaId = table.Column<int>(type: "int", nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: false),
                    ListenerUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailSentDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistReleaseNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistReleaseNotifications_AspNetUsers_ListenerUserId",
                        column: x => x.ListenerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ArtistReleaseNotifications_CreatorPersonas_CreatorPersonaId",
                        column: x => x.CreatorPersonaId,
                        principalTable: "CreatorPersonas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistReleaseNotifications_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ArtistFollowerMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArtistFollowerId = table.Column<int>(type: "int", nullable: false),
                    SenderUserId = table.Column<int>(type: "int", nullable: false),
                    MessageKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MessageText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailSentDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RelatedSongMetadataId = table.Column<int>(type: "int", nullable: true),
                    IsHiddenByListener = table.Column<bool>(type: "bit", nullable: false),
                    IsReported = table.Column<bool>(type: "bit", nullable: false),
                    ReportReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReportedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModerationResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModerationAccepted = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistFollowerMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistFollowerMessages_ArtistFollowers_ArtistFollowerId",
                        column: x => x.ArtistFollowerId,
                        principalTable: "ArtistFollowers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistFollowerMessages_AspNetUsers_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ArtistFollowerMessages_SongMetadata_RelatedSongMetadataId",
                        column: x => x.RelatedSongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id");
                });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistReleaseEmails" },
                values: new object[] { true, true });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistReleaseEmails" },
                values: new object[] { true, true });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_ArtistFollowerId",
                table: "ArtistFollowerMessages",
                column: "ArtistFollowerId",
                unique: true,
                filter: "[MessageKind] = 'ThankYou'");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_EmailSentDateUtc",
                table: "ArtistFollowerMessages",
                column: "EmailSentDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_IsReported_ModerationResolvedAtUtc",
                table: "ArtistFollowerMessages",
                columns: new[] { "IsReported", "ModerationResolvedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_RelatedSongMetadataId",
                table: "ArtistFollowerMessages",
                column: "RelatedSongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowerMessages_SenderUserId",
                table: "ArtistFollowerMessages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowers_CreatorPersonaId",
                table: "ArtistFollowers",
                column: "CreatorPersonaId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowers_CreatorPersonaId_AnonymousListenerNumber",
                table: "ArtistFollowers",
                columns: new[] { "CreatorPersonaId", "AnonymousListenerNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowers_CreatorPersonaId_ListenerUserId",
                table: "ArtistFollowers",
                columns: new[] { "CreatorPersonaId", "ListenerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowers_ListenerUserId",
                table: "ArtistFollowers",
                column: "ListenerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistFollowers_SourceSongMetadataId",
                table: "ArtistFollowers",
                column: "SourceSongMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleaseNotifications_CreatorPersonaId",
                table: "ArtistReleaseNotifications",
                column: "CreatorPersonaId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleaseNotifications_EmailSentDateUtc",
                table: "ArtistReleaseNotifications",
                column: "EmailSentDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleaseNotifications_ListenerUserId_CreatedDateUtc",
                table: "ArtistReleaseNotifications",
                columns: new[] { "ListenerUserId", "CreatedDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleaseNotifications_SongMetadataId_ListenerUserId",
                table: "ArtistReleaseNotifications",
                columns: new[] { "SongMetadataId", "ListenerUserId" },
                unique: true);

            // ---------------------------------------------------------------------------------
            // Backfills. Both are wrapped in EXEC(N'...') and that is not stylistic: the davidtest
            // Web Deploy path puts the whole migration in ONE batch inside one transaction, and
            // SQL Server compiles a batch before running any of it - so a bare UPDATE naming a
            // column the AddColumn above it creates fails to parse with "Invalid column name",
            // even though the same statement applies cleanly via Migrate() at startup. Dynamic SQL
            // is compiled when it runs, by which point the columns exist. AddUserPlaylistSortOrder
            // and AddCreatorStatusAnnouncementFlags both do this; the second learned it by failing
            // a publish.
            // ---------------------------------------------------------------------------------

            // Existing songs are not new releases. Without this every row would have a NULL
            // FirstPublishedAtUtc, the notification job would read that as "never published", and
            // its first run would stamp the entire back catalogue as released today - putting
            // every song ever uploaded inside the 7-day notification window at once.
            //
            // CreatedAt is the honest approximation: nothing recorded the moment a song went
            // public before this column existed, and every value it produces is in the past, which
            // is what matters. (Belt and braces: no follow rows exist yet either, and the job only
            // notifies followers who followed BEFORE a song was published, so day one is silent
            // regardless.)
            migrationBuilder.Sql(
                "EXEC(N'UPDATE SongMetadata SET FirstPublishedAtUtc = CreatedAt " +
                "WHERE FirstPublishedAtUtc IS NULL')");

            // No backfill is needed for the two AspNetUsers flags: AddColumn with a defaultValue
            // on a non-nullable column emits its own UPDATE for existing rows, which is visible in
            // the generated script. Adding a second one here would just be a duplicate.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistFollowerMessages");

            migrationBuilder.DropTable(
                name: "ArtistReleaseNotifications");

            migrationBuilder.DropTable(
                name: "ArtistFollowers");

            migrationBuilder.DropColumn(
                name: "FirstPublishedAtUtc",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "ReceiveArtistMessageEmails",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReceiveArtistReleaseEmails",
                table: "AspNetUsers");
        }
    }
}
