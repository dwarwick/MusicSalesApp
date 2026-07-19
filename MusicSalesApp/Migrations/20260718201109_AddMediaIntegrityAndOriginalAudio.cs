using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaIntegrityAndOriginalAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalAudioBlobPath",
                table: "SongMetadata",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAudioContentType",
                table: "SongMetadata",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAudioFileName",
                table: "SongMetadata",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OriginalAudioFileSize",
                table: "SongMetadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaIntegrityAuditRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SourceRunId = table.Column<int>(type: "int", nullable: true),
                    InitiatedByUserId = table.Column<int>(type: "int", nullable: true),
                    InitiatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HangfireJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CandidateCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    HealthyCount = table.Column<int>(type: "int", nullable: false),
                    RepairableCount = table.Column<int>(type: "int", nullable: false),
                    NamingWarningCount = table.Column<int>(type: "int", nullable: false),
                    OriginalSourceMissingCount = table.Column<int>(type: "int", nullable: false),
                    ConfirmedUnplayableCount = table.Column<int>(type: "int", nullable: false),
                    InconclusiveCount = table.Column<int>(type: "int", nullable: false),
                    RepairedCount = table.Column<int>(type: "int", nullable: false),
                    QuarantinedCount = table.Column<int>(type: "int", nullable: false),
                    NotificationFailureCount = table.Column<int>(type: "int", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AdminNotificationSent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIntegrityAuditRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaIntegrityAuditItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuditRunId = table.Column<int>(type: "int", nullable: false),
                    SongMetadataId = table.Column<int>(type: "int", nullable: true),
                    EffectiveTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PlaybackBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OriginalAudioBlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsOriginalSourceMissing = table.Column<bool>(type: "bit", nullable: false),
                    BlobLength = table.Column<long>(type: "bigint", nullable: true),
                    ETag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BlobLastModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DetectedFormat = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DecodedDuration = table.Column<double>(type: "float", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Diagnostic = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MetadataRepaired = table.Column<bool>(type: "bit", nullable: false),
                    Quarantined = table.Column<bool>(type: "bit", nullable: false),
                    CreatorNotificationSent = table.Column<bool>(type: "bit", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIntegrityAuditItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaIntegrityAuditItems_MediaIntegrityAuditRuns_AuditRunId",
                        column: x => x.AuditRunId,
                        principalTable: "MediaIntegrityAuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaIntegrityAuditItems_SongMetadata_SongMetadataId",
                        column: x => x.SongMetadataId,
                        principalTable: "SongMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.Sql(
                """
                ;WITH NormalizedPaths AS
                (
                    SELECT [Id], REPLACE([Mp3BlobPath], '\\', '/') AS [NormalizedPath]
                    FROM [SongMetadata]
                    WHERE [Mp3BlobPath] IS NOT NULL
                      AND ([SongTitle] IS NULL OR LEN(LTRIM(RTRIM([SongTitle]))) = 0)
                ),
                FileNames AS
                (
                    SELECT [Id], RIGHT([NormalizedPath], CHARINDEX('/', REVERSE([NormalizedPath]) + '/') - 1) AS [FileName]
                    FROM NormalizedPaths
                ),
                DerivedTitles AS
                (
                    SELECT [Id],
                           CASE
                               WHEN CHARINDEX('.', REVERSE([FileName])) > 0
                                   THEN LEFT([FileName], LEN([FileName]) - CHARINDEX('.', REVERSE([FileName])))
                               ELSE [FileName]
                           END AS [DerivedTitle]
                    FROM FileNames
                )
                UPDATE songs
                SET [SongTitle] = CASE
                    WHEN derived.[DerivedTitle] IS NULL OR LEN(LTRIM(RTRIM(derived.[DerivedTitle]))) = 0
                        THEN CONCAT('Song ', songs.[Id])
                    ELSE LEFT(REPLACE(derived.[DerivedTitle], '_', ' '), 200)
                END,
                [StatusReason] = CASE
                    WHEN derived.[DerivedTitle] IS NULL OR LEN(LTRIM(RTRIM(derived.[DerivedTitle]))) = 0
                        THEN 'Media integrity migration: title requires correction.'
                    ELSE songs.[StatusReason]
                END
                FROM [SongMetadata] songs
                INNER JOIN DerivedTitles derived ON derived.[Id] = songs.[Id];
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SongMetadata_AudioRequiresTitle",
                table: "SongMetadata",
                sql: "[Mp3BlobPath] IS NULL OR ([SongTitle] IS NOT NULL AND LTRIM(RTRIM([SongTitle])) <> '')");

            migrationBuilder.CreateIndex(
                name: "IX_MediaIntegrityAuditItems_AuditRunId_SongMetadataId",
                table: "MediaIntegrityAuditItems",
                columns: new[] { "AuditRunId", "SongMetadataId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaIntegrityAuditItems_SongMetadataId",
                table: "MediaIntegrityAuditItems",
                column: "SongMetadataId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaIntegrityAuditItems");

            migrationBuilder.DropTable(
                name: "MediaIntegrityAuditRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SongMetadata_AudioRequiresTitle",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalAudioBlobPath",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalAudioContentType",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalAudioFileName",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "OriginalAudioFileSize",
                table: "SongMetadata");
        }
    }
}
