using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsDraftAndUnpublishAutoPublished : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftTimingsBlobPath",
                table: "SongLyrics",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DraftUpdatedAt",
                table: "SongLyrics",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "SongLyrics",
                type: "datetime2",
                nullable: true);

            // Everything currently Published (1) was published by the pipeline, not by a person.
            // Until now an alignment that cleared the confidence threshold was written straight to
            // Published and shown to listeners with the creator never having heard it. That is the
            // behaviour this release removes, so the rows it already produced are demoted to
            // NeedsReview (2) and wait for their creator like every future one will.
            //
            // Nothing is lost: the timings blob is untouched, so a creator who listens and agrees
            // republishes the identical file with one click. PublishedAt is deliberately left null,
            // so the editor reads "never published" rather than inventing a date nobody chose.
            migrationBuilder.Sql("UPDATE SongLyrics SET Status = 2 WHERE Status = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The status demotion is NOT reversed, and that is deliberate rather than an oversight.
            // A demoted row is indistinguishable from a song that genuinely needs review - both are
            // NeedsReview with timings and no PublishedAt - so re-publishing everything in that state
            // would put timings in front of listeners that no creator ever approved, which is the
            // precise failure this migration exists to end. Rolling back leaves them for their
            // creators, which is the safe direction to be wrong in.

            migrationBuilder.DropColumn(
                name: "DraftTimingsBlobPath",
                table: "SongLyrics");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedAt",
                table: "SongLyrics");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "SongLyrics");
        }
    }
}
