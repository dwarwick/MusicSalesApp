#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistMessageModerationService : IArtistMessageModerationService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IArtistFollowerIdentityService _identityService;
    private readonly ILogger<ArtistMessageModerationService> _logger;

    public ArtistMessageModerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IArtistFollowerIdentityService identityService,
        ILogger<ArtistMessageModerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _identityService = identityService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportedArtistMessageDto>> GetReportedMessagesAsync(
        bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.ArtistFollowerMessages
            .AsNoTracking()
            .Where(message => message.IsReported);

        if (!includeResolved)
        {
            query = query.Where(message => message.ModerationResolvedAtUtc == null);
        }

        var rows = await query
            .OrderBy(message => message.ModerationResolvedAtUtc == null ? 0 : 1)
            .ThenByDescending(message => message.ReportedAtUtc)
            .Select(message => new
            {
                message.Id,
                message.ArtistFollower.CreatorPersonaId,
                ArtistName = message.ArtistFollower.CreatorPersona.Name,
                // Even here the reporter is a pseudonym. An admin who genuinely needs the account
                // has the database; a review screen does not, and every extra place an identity is
                // rendered is another place it can be screenshotted or logged.
                message.ArtistFollower.AnonymousListenerNumber,
                message.MessageText,
                message.ReportReason,
                message.ReportedAtUtc,
                message.CreatedDateUtc,
                message.ModerationResolvedAtUtc,
                message.ModerationAccepted,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ReportedArtistMessageDto(
            row.Id,
            row.CreatorPersonaId,
            string.IsNullOrWhiteSpace(row.ArtistName) ? ArtistDisplayNames.UnknownArtist : row.ArtistName,
            _identityService.FormatDisplayName(row.AnonymousListenerNumber),
            row.MessageText,
            row.ReportReason,
            row.ReportedAtUtc,
            row.CreatedDateUtc,
            row.ModerationResolvedAtUtc,
            row.ModerationAccepted)).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> ResolveReportAsync(
        int messageId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var message = await context.ArtistFollowerMessages
            .FirstOrDefaultAsync(row => row.Id == messageId && row.IsReported, cancellationToken);

        if (message is null)
        {
            return false;
        }

        message.ModerationResolvedAtUtc = DateTime.UtcNow;
        message.ModerationAccepted = accepted;

        if (accepted)
        {
            // Upholding a report takes the message out of the listener's list. The row survives so
            // the decision stays auditable and the creator's rate-limit history is unchanged.
            message.IsHiddenByListener = true;
        }

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Artist message {MessageId} report resolved as {Outcome}.",
            messageId,
            accepted ? "accepted" : "rejected");

        return true;
    }
}
