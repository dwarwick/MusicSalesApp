#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Data;

/// <summary>
/// Stamps <see cref="SongMetadata.FirstPublishedAtUtc"/> at the moment a song actually becomes
/// publicly visible.
/// </summary>
/// <remarks>
/// <para>
/// The whole artist-follow feature keys off that timestamp - the release job asks "was this
/// follower already following when it came out?" - so it has to mean the moment of publication
/// rather than the moment somebody noticed. It used to be written only by the hourly release job,
/// which left a gap of up to an hour in which a listener who discovered a brand-new song and
/// followed because of it counted as having followed BEFORE it was published, and so was sent a
/// notification about the song they had just followed for.
/// </para>
/// <para>
/// An interceptor rather than a line in the publishing code because there is no single publishing
/// code path: a song goes public from the upload pipeline, from the transcode callback that fills
/// in Mp3BlobPath, from the admin song page and from SongStatusService, and any future path would
/// have to remember. This sits under all of them.
/// </para>
/// <para>
/// It cannot see a bulk <c>ExecuteUpdate</c>, which bypasses the change tracker by design. That is
/// why the release job keeps its stamping pass - now as a backstop that logs a Warning if it ever
/// finds anything, since finding something means a write got past this.
/// </para>
/// </remarks>
public sealed class SongPublicationStampInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<SongMetadata>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var song = entry.Entity;

            // Stamped exactly once, ever. Re-stamping on a later edit is what would turn a re-crop,
            // a lyrics change or a title fix into a second "new release" for every follower.
            if (song.FirstPublishedAtUtc is not null || !song.IsPubliclyReleased())
            {
                continue;
            }

            song.FirstPublishedAtUtc = now;
        }
    }
}
