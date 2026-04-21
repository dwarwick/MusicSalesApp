using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

    public AccountDeletionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ICreatorService creatorService,
        ICreatorPersonaService creatorPersonaService,
        UserManager<ApplicationUser> userManager,
        ILogger<AccountDeletionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _creatorService = creatorService;
        _creatorPersonaService = creatorPersonaService;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<IdentityResult> DeleteAccountAsync(ApplicationUser user)
    {
        var userId = user.Id;
        var creatorId = await _creatorService.GetCreatorIdForUserAsync(userId);

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
}
