using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorStatusAnnouncementFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivationAnnouncedAt",
                table: "Creators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeactivationAnnouncedAt",
                table: "Creators",
                type: "datetime2",
                nullable: true);

            // Null means "this announcement is still owed". Without this backfill every
            // creator who activated before this migration would be congratulated again on
            // their next visit - and that celebration fires a Google Ads conversion, a funnel
            // event and a user-history row. They have all already seen it, so it is not owed.
            //
            // Wrapped in EXEC deliberately. SQL Server compiles a whole batch before running
            // any of it, so a plain UPDATE naming columns the statements above have not
            // created yet fails at parse time with "Invalid column name" - which is exactly
            // how this broke a Web Deploy publish. Dynamic SQL is compiled when it runs, by
            // which point the columns exist.
            migrationBuilder.Sql(
                @"EXEC(N'UPDATE [Creators]
                          SET [ActivationAnnouncedAt] = SYSUTCDATETIME(),
                              [DeactivationAnnouncedAt] = SYSUTCDATETIME()')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivationAnnouncedAt",
                table: "Creators");

            migrationBuilder.DropColumn(
                name: "DeactivationAnnouncedAt",
                table: "Creators");
        }
    }
}
