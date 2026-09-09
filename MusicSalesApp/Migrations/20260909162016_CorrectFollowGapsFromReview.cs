using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <summary>
    /// Three corrections from the artist-follow code review: a column so a returning follower is
    /// not told what they missed, the index the hourly release job always needed, and undoing the
    /// part of the original backfill that stamped songs which were never public.
    /// </summary>
    public partial class CorrectFollowGapsFromReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Re-following reactivates the original row and deliberately leaves FollowedDateUtc
            // alone, because that is the creator's "Following Since". That made it the wrong field
            // for "were they following when this came out": someone who followed in January, left
            // in February and came back in March was sent March's releases from while they were
            // away, because January still predates them. The release job reads this instead.
            migrationBuilder.AddColumn<DateTime>(
                name: "ResumedFollowingDateUtc",
                table: "ArtistFollowers",
                type: "datetime2",
                nullable: true);

            // ArtistReleaseNotificationService reads FirstPublishedAtUtc twice as its leading
            // predicate every hour, and there was no index for it - two full catalogue scans an
            // hour, forever, invisible behind a log line that says "Considered 0 release(s)".
            migrationBuilder.CreateIndex(
                name: "IX_SongMetadata_FirstPublishedAtUtc",
                table: "SongMetadata",
                column: "FirstPublishedAtUtc");

            // AddArtistFollowFeature backfilled FirstPublishedAtUtc = CreatedAt for EVERY row, with
            // no publicly-released predicate. A song that was still a draft then - not enabled, or
            // still transcoding so Mp3BlobPath was null, or with no persona yet - was therefore
            // marked as already released. When it does go live, step 1 of the job skips it (the
            // column is not null) and step 2 excludes it (CreatedAt is outside the seven-day
            // window), so its followers are never told. Silently, and permanently.
            //
            // Nulling it puts those songs back where they belong: SongPublicationStampInterceptor
            // stamps them the moment they actually go public.
            //
            // Three conditions, each of them narrowing this to exactly the damage the backfill did:
            //
            //   FirstPublishedAtUtc = CreatedAt   only rows still carrying the backfilled value. A
            //                                     song the job has stamped since has a different
            //                                     one, and that value is real.
            //   NOT publicly released             a song that IS public today was correctly
            //                                     stamped, whatever the date says.
            //   no notification rows              distinguishes "never published" from "published,
            //                                     told its followers, then withdrawn" - the second
            //                                     must keep its date or re-enabling it would
            //                                     announce it a second time.
            migrationBuilder.Sql(@"
                UPDATE SongMetadata
                SET FirstPublishedAtUtc = NULL
                WHERE FirstPublishedAtUtc IS NOT NULL
                  AND FirstPublishedAtUtc = CreatedAt
                  AND NOT (IsActive = 1
                           AND IsEnabled = 1
                           AND IsAlbumCover = 0
                           AND Mp3BlobPath IS NOT NULL
                           AND Mp3BlobPath <> ''
                           AND PersonaId IS NOT NULL)
                  AND NOT EXISTS (
                      SELECT 1 FROM ArtistReleaseNotifications n
                      WHERE n.SongMetadataId = SongMetadata.Id)");
        }

        /// <inheritdoc />
        /// <remarks>
        /// The UPDATE above is not reversed, and cannot be: re-stamping those songs would need the
        /// CreatedAt values it replaced, and putting them back would restore the bug. Rolling back
        /// leaves them null, which is the correct state either way - the job stamps them when they
        /// go public, exactly as it does for any song uploaded after this migration.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SongMetadata_FirstPublishedAtUtc",
                table: "SongMetadata");

            migrationBuilder.DropColumn(
                name: "ResumedFollowingDateUtc",
                table: "ArtistFollowers");
        }
    }
}
