using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    MessageText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SendEmail = table.Column<bool>(type: "bit", nullable: false),
                    ShowDialog = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CanceledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CanceledByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminMessages_AspNetUsers_CanceledByUserId",
                        column: x => x.CanceledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AdminMessages_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AdminMessageRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AdminMessageId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    EmailAddressSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DialogDeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CanceledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminMessageRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminMessageRecipients_AdminMessages_AdminMessageId",
                        column: x => x.AdminMessageId,
                        principalTable: "AdminMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdminMessageRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdminMessageRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AdminMessageId = table.Column<int>(type: "int", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminMessageRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminMessageRoles_AdminMessages_AdminMessageId",
                        column: x => x.AdminMessageId,
                        principalTable: "AdminMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRecipients_AcknowledgedAtUtc",
                table: "AdminMessageRecipients",
                column: "AcknowledgedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRecipients_AdminMessageId_UserId",
                table: "AdminMessageRecipients",
                columns: new[] { "AdminMessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRecipients_CanceledAtUtc",
                table: "AdminMessageRecipients",
                column: "CanceledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRecipients_EmailSentAtUtc",
                table: "AdminMessageRecipients",
                column: "EmailSentAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRecipients_UserId",
                table: "AdminMessageRecipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessageRoles_AdminMessageId_RoleName",
                table: "AdminMessageRoles",
                columns: new[] { "AdminMessageId", "RoleName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessages_CanceledAtUtc",
                table: "AdminMessages",
                column: "CanceledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessages_CanceledByUserId",
                table: "AdminMessages",
                column: "CanceledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessages_CreatedAtUtc",
                table: "AdminMessages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMessages_CreatedByUserId",
                table: "AdminMessages",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminMessageRecipients");

            migrationBuilder.DropTable(
                name: "AdminMessageRoles");

            migrationBuilder.DropTable(
                name: "AdminMessages");
        }
    }
}
