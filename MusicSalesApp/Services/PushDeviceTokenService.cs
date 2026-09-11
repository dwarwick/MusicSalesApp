#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class PushDeviceTokenService : IPushDeviceTokenService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<PushDeviceTokenService> _logger;

    public PushDeviceTokenService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<PushDeviceTokenService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> RegisterAsync(
        int userId,
        string platform,
        string token,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = PushPlatforms.Normalize(platform);

        if (normalizedPlatform is null || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        token = token.Trim();

        if (token.Length > 512)
        {
            // Refuse rather than truncate. A truncated token is accepted here and then fails every
            // send forever, which is far harder to diagnose than a rejected registration.
            _logger.LogWarning("Refused a push token of {Length} characters as over-long.", token.Length);
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var existing = await context.PushDeviceTokens
            .FirstOrDefaultAsync(row => row.Token == token, cancellationToken);

        if (existing is not null)
        {
            // The reassignment case. Whoever registered it last is who it belongs to now.
            existing.UserId = userId;
            existing.Platform = normalizedPlatform;
            existing.DeviceId = deviceId;
            existing.LastSeenAtUtc = now;
            existing.IsActive = true;
            existing.DeactivatedAtUtc = null;
            existing.DeactivationReason = null;

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            // Same install, rotated token. Update in place so the old token does not linger as a
            // second row that can never be delivered to.
            var sameDevice = await context.PushDeviceTokens
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.DeviceId == deviceId, cancellationToken);

            if (sameDevice is not null)
            {
                sameDevice.Token = token;
                sameDevice.Platform = normalizedPlatform;
                sameDevice.LastSeenAtUtc = now;
                sameDevice.IsActive = true;
                sameDevice.DeactivatedAtUtc = null;
                sameDevice.DeactivationReason = null;

                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
        }

        context.PushDeviceTokens.Add(new PushDeviceToken
        {
            UserId = userId,
            Platform = normalizedPlatform,
            Token = token,
            DeviceId = deviceId,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            IsActive = true,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Ask what actually happened rather than assuming.
            //
            // The case worth surviving is two registrations for one token racing - the app
            // registers on launch and again on an auth change, and those can overlap. The unique
            // index refuses the loser, but the winner wrote the row we wanted, so from the caller's
            // point of view it succeeded.
            //
            // Every OTHER thing DbUpdateException wraps - a foreign key violation because the
            // account went away mid-call, a truncation, a deadlock victim, a timeout - wrote
            // nothing. This used to return true for all of them, which told the phone it was
            // registered when it was not; the client then stops retrying and the device is dark
            // forever, looking exactly like the two server-side gates being off. That is the
            // wrong-diagnosis trap the push documentation exists to prevent.
            //
            // Re-reading answers it without inspecting provider-specific error numbers, and matches
            // how ArtistFollowService settles the same kind of race.
            context.ChangeTracker.Clear();

            var winner = await context.PushDeviceTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Token == token, cancellationToken);

            if (winner is not null)
            {
                _logger.LogDebug(ex, "Concurrent push token registration; the existing row stands.");
                return true;
            }

            _logger.LogError(ex, "Push token registration failed; no row was written.");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(
        int userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Scoped to the caller: a client must not be able to silence someone else's device by
        // guessing or replaying a token.
        var row = await context.PushDeviceTokens
            .FirstOrDefaultAsync(
                candidate => candidate.Token == token.Trim() && candidate.UserId == userId,
                cancellationToken);

        if (row is null)
        {
            return false;
        }

        row.IsActive = false;
        row.DeactivatedAtUtc = DateTime.UtcNow;
        row.DeactivationReason = "Unregistered by the client";

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, List<PushDeviceToken>>> GetActiveTokensAsync(
        IEnumerable<int> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds?.Distinct().ToList() ?? [];

        if (ids.Count == 0)
        {
            return new Dictionary<int, List<PushDeviceToken>>();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.PushDeviceTokens
            .AsNoTracking()
            .Where(row => row.IsActive && ids.Contains(row.UserId))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <inheritdoc />
    public async Task<int> DeactivateAsync(
        IEnumerable<string> tokens,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var list = tokens?.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct().ToList() ?? [];

        if (list.Count == 0)
        {
            return 0;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var trimmedReason = reason.Length > 100 ? reason[..100] : reason;

        var affected = await context.PushDeviceTokens
            .Where(row => list.Contains(row.Token) && row.IsActive)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.IsActive, false)
                    .SetProperty(row => row.DeactivatedAtUtc, now)
                    .SetProperty(row => row.DeactivationReason, trimmedReason),
                cancellationToken);

        if (affected > 0)
        {
            _logger.LogInformation("Deactivated {Count} push tokens: {Reason}", affected, trimmedReason);
        }

        return affected;
    }
}
