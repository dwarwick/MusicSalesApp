using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class UnverifiedUserCleanupService : IUnverifiedUserCleanupService
{
    private static readonly TimeSpan StaleAccountAge = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly ILogger<UnverifiedUserCleanupService> _logger;

    public UnverifiedUserCleanupService(
        IDbContextFactory<AppDbContext> contextFactory,
        IAccountDeletionService accountDeletionService,
        ILogger<UnverifiedUserCleanupService> logger)
    {
        _contextFactory = contextFactory;
        _accountDeletionService = accountDeletionService;
        _logger = logger;
    }

    public async Task<int> DeleteStaleUnverifiedUsersAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var cutoff = DateTime.UtcNow.Subtract(StaleAccountAge);

            var staleUsers = await context.Users
                .Where(user => !user.EmailConfirmed)
                .Where(user => context.UserRoles.Any(userRole =>
                    userRole.UserId == user.Id &&
                    context.Roles.Any(role => role.Id == userRole.RoleId && role.Name == Roles.NonValidatedUser)))
                .Where(user => context.UserHistories.Any(history =>
                    history.UserId == user.Id &&
                    history.EventType == UserHistoryEventTypes.Registration &&
                    history.OccurredAt <= cutoff))
                .ToListAsync();

            if (staleUsers.Count == 0)
            {
                _logger.LogInformation("No stale unverified users found for cleanup");
                return 0;
            }

            _logger.LogInformation(
                "Found {Count} stale unverified users registered on or before {Cutoff}",
                staleUsers.Count,
                cutoff);

            var deletedCount = 0;
            foreach (var user in staleUsers)
            {
                var result = await _accountDeletionService.DeleteAccountAsync(user);
                if (result.Succeeded)
                {
                    deletedCount++;
                    continue;
                }

                _logger.LogWarning(
                    "Failed to delete stale unverified user {UserId}: {Errors}",
                    user.Id,
                    string.Join(';', result.Errors.Select(error => error.Description)));
            }

            _logger.LogInformation("Deleted {Count} stale unverified users", deletedCount);
            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stale unverified users");
            throw;
        }
    }
}