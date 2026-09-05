#nullable enable
using Microsoft.EntityFrameworkCore;
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
                // A suspended user is shown both switches off, whatever the columns say. The email
                // jobs already skip them, so reporting the stored value would tell someone they
                // were subscribed to mail they will not receive.
                ReceiveArtistReleaseEmails = user.ReceiveArtistReleaseEmails && !user.IsSuspended,
                ReceiveArtistMessageEmails = user.ReceiveArtistMessageEmails && !user.IsSuspended,
                ReceiveArtistReleasePush = user.ReceiveArtistReleasePush && !user.IsSuspended,
                ReceiveArtistMessagePush = user.ReceiveArtistMessagePush && !user.IsSuspended,
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

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
