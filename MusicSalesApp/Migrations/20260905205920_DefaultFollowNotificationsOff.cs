using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class DefaultFollowNotificationsOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistMessagePush", "ReceiveArtistReleaseEmails", "ReceiveArtistReleasePush" },
                values: new object[] { false, false, false, false });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistMessagePush", "ReceiveArtistReleaseEmails", "ReceiveArtistReleasePush" },
                values: new object[] { false, false, false, false });

            // EF only scaffolded the two seeded users above, because the column DEFAULTS were set
            // by hand in AddArtistFollowFeature and AddPushNotifications rather than through the
            // model - so the model diff cannot see them. Both halves have to be said explicitly.

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistReleaseEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistMessageEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistReleasePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistMessagePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            // And flip the rows that already exist. Safe to do wholesale because the follow feature
            // has not shipped: these columns arrived on this branch, so no listener has expressed a
            // preference for this to overwrite. If it HAD shipped, only the default should change.
            //
            // No EXEC(N'...') wrapper here, unlike the earlier follow migrations: that is needed
            // when a statement names a column the same migration creates, because SQL Server parses
            // a whole batch before running any of it. These columns already exist.
            migrationBuilder.Sql(
                "UPDATE AspNetUsers SET " +
                "ReceiveArtistReleaseEmails = 0, ReceiveArtistMessageEmails = 0, " +
                "ReceiveArtistReleasePush = 0, ReceiveArtistMessagePush = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistMessagePush", "ReceiveArtistReleaseEmails", "ReceiveArtistReleasePush" },
                values: new object[] { true, true, true, true });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "ReceiveArtistMessageEmails", "ReceiveArtistMessagePush", "ReceiveArtistReleaseEmails", "ReceiveArtistReleasePush" },
                values: new object[] { true, true, true, true });

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistReleaseEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistMessageEmails",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistReleasePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "ReceiveArtistMessagePush",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);
        }
    }
}
