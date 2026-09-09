#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistNotificationPreferenceService : IArtistNotificationPreferenceService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public ArtistNotificationPreferenceService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public async Task<ArtistNotificationPreferences?> GetAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new ArtistNotificationPreferences
            {
                // Every field is reported AS STORED, including for a suspended account.
                //
                // These four used to be masked to false while suspended, on the reasoning that
                // reporting them promises mail nobody will receive. The reasoning was sound and the
                // mask was still wrong, because this is half of a GET/PUT pair over ONE record: the
                // app reads all five, the listener toggles one, and the app writes all five back.
                // A masked read therefore did not merely mislead, it DELETED - the four untouched
                // preferences came back false and were persisted that way, so lifting a suspension
                // restored an account opted out of everything with no record it ever was not.
                //
                // The same argument was already written down one field below for ArtistPushFrequency
                // and simply never carried across to its four neighbours. Nothing is lost by
                // answering honestly: suspension is enforced where it matters, in the email jobs and
                // the push dispatcher, both of which check IsSuspended for themselves.
                ReceiveArtistReleaseEmails = user.ReceiveArtistReleaseEmails,
                ReceiveArtistMessageEmails = user.ReceiveArtistMessageEmails,
                ReceiveArtistReleasePush = user.ReceiveArtistReleasePush,
                ReceiveArtistMessagePush = user.ReceiveArtistMessagePush,

                // Reported as stored. It is not a claim that anything will be delivered, and
                // blanking it to Instant would silently discard a choice the moment an account was
                // suspended, then restore the wrong one when it came back.
                ArtistPushFrequency = ArtistPushFrequencies.FromValue(user.ArtistPushFrequency),
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        int userId,
        ArtistNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (preferences is null)
        {
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var user = await context.Users.FirstOrDefaultAsync(row => row.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.ReceiveArtistReleaseEmails = preferences.ReceiveArtistReleaseEmails;
        user.ReceiveArtistMessageEmails = preferences.ReceiveArtistMessageEmails;
        user.ReceiveArtistReleasePush = preferences.ReceiveArtistReleasePush;
        user.ReceiveArtistMessagePush = preferences.ReceiveArtistMessagePush;

        // Normalised on the way in: the column is an int, and a value outside the enum would make
        // the dispatcher's window lookup fall back to Instant on every run rather than once here.
        user.ArtistPushFrequency = (int)ArtistPushFrequencies.FromValue((int)preferences.ArtistPushFrequency);

        // Rotated because this writes AspNetUsers outside Identity, which rotates it for itself.
        // Without this, a /manage-account page opened before this call still carried a matching
        // stamp, so its UserManager.UpdateAsync - which writes every mapped column, including the
        // two push preferences this page has no control for - passed the concurrency check and
        // silently reverted whatever was just set here from the phone.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
