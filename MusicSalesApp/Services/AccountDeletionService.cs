using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class AccountDeletionService : IAccountDeletionService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ICreatorService _creatorService;
    private readonly ICreatorPersonaService _creatorPersonaService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AccountDeletionService> _logger;
    private readonly IAppleTokenRevocationService _appleTokenRevocationService;

    public AccountDeletionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ICreatorService creatorService,
        ICreatorPersonaService creatorPersonaService,
        UserManager<ApplicationUser> userManager,
        ILogger<AccountDeletionService> logger,
        IAppleTokenRevocationService appleTokenRevocationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _creatorService = creatorService;
        _creatorPersonaService = creatorPersonaService;
        _userManager = userManager;
        _logger = logger;
        _appleTokenRevocationService = appleTokenRevocationService;
    }

    public async Task<IdentityResult> DeleteAccountAsync(ApplicationUser user)
    {
        var userId = user.Id;
        var creator = await _creatorService.GetCreatorByUserIdAsync(userId);
        if (creator?.IsActive == true)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = AccountDeletionErrorCodes.ActiveCreatorMustStopSellingFirst,
                Description = "You must stop being a creator before deleting your account."
            });
        }

        var creatorId = creator?.Id;

        // Apple requires an app offering Sign in with Apple to revoke the user's grant when the
        // account is deleted. Do it before the row goes, because the token is unrecoverable after.
        await RevokeAppleGrantAsync(user);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Nullify SongStream.StreamerUserId (nullable FK, NoAction)
        await dbContext.SongStreams
            .Where(ss => ss.StreamerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(ss => ss.StreamerUserId, (int?)null));

        // Delete UserPlaylist rows (required FK, NoAction)
        await dbContext.UserPlaylists
            .Where(up => up.UserId == userId)
            .ExecuteDeleteAsync();

        // Delete the user's Playlist rows (both system and custom) — the user
        // is being deleted, so all of their playlists go with them.
        await dbContext.Playlists
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        // Delete Tips where user is the tipper (required FK, NoAction)
        await dbContext.Tips
            .Where(t => t.TipperUserId == userId)
            .ExecuteDeleteAsync();

        // Delete BlockedTipAttempts where user is the tipper (required FK, NoAction)
        await dbContext.BlockedTipAttempts
            .Where(b => b.TipperUserId == userId)
            .ExecuteDeleteAsync();

        // Delete report rows where the user reported a song (required FK, NoAction)
        await dbContext.ReportedSongs
            .Where(r => r.ReportingUserId == userId)
            .ExecuteDeleteAsync();

        // Follow-feature rows for this user as a LISTENER. ArtistFollower.ListenerUserId and
        // ArtistReleaseNotification.ListenerUserId are both NoAction, so nothing removes these on
        // their own - and leaving them would keep a deleted account present in creators' follower
        // counts. Messages hang off ArtistFollower and go with it by cascade.
        await dbContext.ArtistReleaseNotifications
            .Where(notification => notification.ListenerUserId == user.Id)
            .ExecuteDeleteAsync();

        // Messages this user SENT as a creator. SenderUserId is NoAction, and these rows sit on
        // other people's follow relationships, so the ArtistFollower delete below does not reach
        // them and the user row cannot go until they do.
        await dbContext.ArtistFollowerMessages
            .Where(message => message.SenderUserId == user.Id)
            .ExecuteDeleteAsync();

        await dbContext.ArtistFollowers
            .Where(follow => follow.ListenerUserId == user.Id)
            .ExecuteDeleteAsync();

        // Delete any pending mobile verification codes for this user.
        await dbContext.MobileVerificationCodes
            .Where(code => code.UserId == userId)
            .ExecuteDeleteAsync();

        // If user is a creator, clean up Creator-referencing NoAction FKs
        // so the User → Creator cascade delete can proceed
        if (creatorId.HasValue)
        {
            // Nullify SongStream.CreatorId (nullable FK, NoAction)
            await dbContext.SongStreams
                .Where(ss => ss.CreatorId == creatorId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(ss => ss.CreatorId, (int?)null));

            // Delete Tips where user is the creator (required FK, NoAction)
            await dbContext.Tips
                .Where(t => t.CreatorId == creatorId.Value)
                .ExecuteDeleteAsync();

            // Delete BlockedTipAttempts where user is the creator (required FK, NoAction)
            await dbContext.BlockedTipAttempts
                .Where(b => b.CreatorId == creatorId.Value)
                .ExecuteDeleteAsync();

            // Delete creator personas and their images
            try
            {
                await _creatorPersonaService.DeleteAllPersonasForCreatorAsync(creatorId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete creator personas before account deletion for user {UserId}", userId);
            }
        }

        return await _userManager.DeleteAsync(user);
    }
    /// <summary>
    /// Best-effort: a failure here must not strand the user with an account they asked us to
    /// delete. Nothing happens for users who never signed in with Apple, or before a Sign in with
    /// Apple key is configured.
    /// </summary>
    private async Task RevokeAppleGrantAsync(ApplicationUser user)
    {
        if (_appleTokenRevocationService?.IsConfigured != true
            || string.IsNullOrWhiteSpace(user.AppleRefreshToken))
        {
            return;
        }

        try
        {
            var revoked = await _appleTokenRevocationService.RevokeRefreshTokenAsync(user.AppleRefreshToken);
            if (revoked)
            {
                _logger.LogInformation("Revoked the Sign in with Apple grant for user {UserId}", user.Id);
            }
            else
            {
                _logger.LogWarning("Could not revoke the Sign in with Apple grant for user {UserId}", user.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign in with Apple revocation threw for user {UserId}", user.Id);
        }
    }
}
