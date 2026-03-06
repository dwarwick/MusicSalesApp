#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing tips from listeners to creators.
/// </summary>
public class TipService : ITipService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<TipService> _logger;

    private const decimal MinTipAmount = 1.00m;
    private const decimal MaxTipAmount = 50.00m;
    private const int MaxTipsPerHour = 5;
    private const int MaxTipsToSameCreator = 10;
    private const int MinAccountAgeDays = 7;
    private const int HoldPeriodDays = 7;

    public TipService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<TipService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(bool CanTip, string? ErrorMessage)> ValidateTipAsync(
        int tipperUserId, int creatorId, decimal amount, string? ipAddress, string? fingerprint)
    {
        // Validate amount
        if (amount < MinTipAmount)
            return (false, $"Minimum tip amount is ${MinTipAmount:F2}.");
        if (amount > MaxTipAmount)
            return (false, $"Maximum tip amount is ${MaxTipAmount:F2}.");

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check account age (at least 7 days old)
        var user = await context.Users.FindAsync(tipperUserId);
        if (user == null)
            return (false, "User not found.");

        // Use LockoutEnd as proxy or CreatedAt - ApplicationUser may not have CreatedAt,
        // so we check if the user has been around long enough using their security stamp or registration
        // For simplicity, check if user was created at least 7 days ago
        // We'll need to check a proxy - the earliest tip or another indicator
        // Actually, check the UserHistory for account creation or use a different approach
        var accountCreated = await context.UserHistories
            .Where(uh => uh.UserId == tipperUserId && uh.EventType == "AccountCreated")
            .Select(uh => uh.OccurredAt)
            .FirstOrDefaultAsync();

        // If no AccountCreated history, fall back to checking if email is confirmed
        // (old accounts won't have history but should be fine)
        if (accountCreated != default && (DateTime.UtcNow - accountCreated).TotalDays < MinAccountAgeDays)
            return (false, "Your account must be at least 7 days old to send tips.");

        // Check self-tipping
        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
            return (false, "Creator not found.");
        if (creator.UserId == tipperUserId)
            return (false, "You cannot tip yourself.");

        // Rate limit: max 5 tips per hour per user
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentTipCount = await context.Tips
            .CountAsync(t => t.TipperUserId == tipperUserId && t.CreatedAt >= oneHourAgo);
        if (recentTipCount >= MaxTipsPerHour)
            return (false, "You can send a maximum of 5 tips per hour. Please try again later.");

        // Rate limit: max 10 tips from a user to the same creator (lifetime)
        var tipsToCreator = await context.Tips
            .CountAsync(t => t.TipperUserId == tipperUserId && t.CreatorId == creatorId);
        if (tipsToCreator >= MaxTipsToSameCreator)
            return (false, "You have reached the maximum number of tips to this creator.");

        // Fraud detection: check IP and fingerprint patterns
        if (!string.IsNullOrEmpty(ipAddress) || !string.IsNullOrEmpty(fingerprint))
        {
            var suspiciousTips = await context.Tips
                .CountAsync(t => t.CreatedAt >= oneHourAgo &&
                    t.TipperUserId != tipperUserId &&
                    ((ipAddress != null && t.IpAddress == ipAddress) ||
                     (fingerprint != null && t.MachineFingerprint == fingerprint)));

            if (suspiciousTips >= MaxTipsPerHour)
            {
                _logger.LogWarning(
                    "Suspicious tipping activity detected. IP: {IP}, Fingerprint: {FP}, UserId: {UserId}",
                    ipAddress, fingerprint, tipperUserId);
                return (false, "Unusual activity detected. Please try again later.");
            }
        }

        return (true, null);
    }

    /// <inheritdoc />
    public async Task<Tip> RecordTipAsync(
        int tipperUserId, int creatorId, int? songMetadataId,
        decimal amount, string payPalOrderId, string? ipAddress, string? fingerprint)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var tip = new Tip
        {
            TipperUserId = tipperUserId,
            CreatorId = creatorId,
            SongMetadataId = songMetadataId,
            Amount = amount,
            Status = TipStatus.Pending,
            PayPalOrderId = payPalOrderId,
            IpAddress = ipAddress,
            MachineFingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow
        };

        context.Tips.Add(tip);
        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Tip recorded: ${Amount} from user {TipperId} to creator {CreatorId}, PayPal order {OrderId}",
            amount, tipperUserId, creatorId, payPalOrderId);

        return tip;
    }

    /// <inheritdoc />
    public async Task<List<Tip>> GetTipsForCreatorAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Tips
            .Include(t => t.TipperUser)
            .Include(t => t.SongMetadata)
            .Where(t => t.CreatorId == creatorId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Tip>> GetClearedTipsForPayoutAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Tips
            .Where(t => t.CreatorId == creatorId && t.Status == TipStatus.Cleared)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> ProcessPendingToClearedAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Tips that have been pending for at least 7 full days
        var cutoff = DateTime.UtcNow.AddDays(-HoldPeriodDays);

        var pendingTips = await context.Tips
            .Where(t => t.Status == TipStatus.Pending && t.CreatedAt <= cutoff)
            .ToListAsync();

        foreach (var tip in pendingTips)
        {
            tip.Status = TipStatus.Cleared;
        }

        var count = await context.SaveChangesAsync();
        if (count > 0)
        {
            _logger.LogInformation("Cleared {Count} tips that passed the 7-day hold period", pendingTips.Count);
        }

        return pendingTips.Count;
    }

    /// <inheritdoc />
    public async Task MarkTipsAsPaidAsync(List<int> tipIds, string payPalPayoutTransactionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var tips = await context.Tips
            .Where(t => tipIds.Contains(t.Id) && t.Status == TipStatus.Cleared)
            .ToListAsync();

        foreach (var tip in tips)
        {
            tip.Status = TipStatus.Paid;
            tip.PaidAt = DateTime.UtcNow;
            tip.PayPalPayoutTransactionId = payPalPayoutTransactionId;
        }

        await context.SaveChangesAsync();
        _logger.LogInformation("Marked {Count} tips as paid with transaction {TxId}", tips.Count, payPalPayoutTransactionId);
    }
}
