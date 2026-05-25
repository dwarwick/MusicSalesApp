#nullable enable

using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class ContactRequestRateLimitService : IContactRequestRateLimitService
{
    internal const int MinimumMinutesBetweenSubmissions = 10;
    internal const int MaxSubmissionsPerUserPerDay = 3;
    internal const int MaxSubmissionsPerIpPerDay = 20;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContactRequestRateLimitService> _logger;

    public ContactRequestRateLimitService(
        IDbContextFactory<AppDbContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<ContactRequestRateLimitService> logger)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ContactRequestReservationResult> TryReserveSubmissionAsync(
        int userId,
        string userEmail,
        string subject,
        string message,
        string? ipAddress)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var shortWindowStart = now.AddMinutes(-MinimumMinutesBetweenSubmissions);
        var dailyWindowStart = now.AddDays(-1);
        var normalizedIpAddress = NormalizeIpAddress(ipAddress);
        var trimmedMessage = message.Trim();

        await using var context = await _contextFactory.CreateDbContextAsync();

        var hasRecentSubmission = await context.ContactRequestSubmissions
            .AnyAsync(submission => submission.UserId == userId && submission.SubmittedAtUtc >= shortWindowStart);
        if (hasRecentSubmission)
        {
            _logger.LogInformation("Blocked contact request for user {UserId}: submitted too recently.", userId);
            return ContactRequestReservationResult.Blocked($"Please wait at least {MinimumMinutesBetweenSubmissions} minutes before sending another message.");
        }

        var userDailyCount = await context.ContactRequestSubmissions
            .CountAsync(submission => submission.UserId == userId && submission.SubmittedAtUtc >= dailyWindowStart);
        if (userDailyCount >= MaxSubmissionsPerUserPerDay)
        {
            _logger.LogInformation("Blocked contact request for user {UserId}: daily user limit reached.", userId);
            return ContactRequestReservationResult.Blocked("You have reached the daily contact form limit. Please try again tomorrow.");
        }

        if (!string.IsNullOrWhiteSpace(normalizedIpAddress))
        {
            var ipDailyCount = await context.ContactRequestSubmissions
                .CountAsync(submission => submission.IpAddress == normalizedIpAddress && submission.SubmittedAtUtc >= dailyWindowStart);
            if (ipDailyCount >= MaxSubmissionsPerIpPerDay)
            {
                _logger.LogWarning("Blocked contact request for user {UserId}: daily IP limit reached for {IpAddress}.", userId, normalizedIpAddress);
                return ContactRequestReservationResult.Blocked("We are receiving too many contact requests from your network. Please try again later.");
            }
        }

        var submission = new ContactRequestSubmission
        {
            UserId = userId,
            UserEmail = userEmail,
            Subject = subject,
            MessageText = trimmedMessage,
            MessageLength = trimmedMessage.Length,
            IpAddress = normalizedIpAddress,
            SubmittedAtUtc = now
        };

        context.ContactRequestSubmissions.Add(submission);
        await context.SaveChangesAsync();

        return ContactRequestReservationResult.Allowed(submission.Id);
    }

    public async Task MarkEmailResultAsync(int submissionId, bool userEmailSent, bool adminEmailSent)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var submission = await context.ContactRequestSubmissions.FindAsync(submissionId);
        if (submission == null)
        {
            _logger.LogWarning("Could not update contact submission {SubmissionId} because it was not found.", submissionId);
            return;
        }

        submission.UserEmailSent = userEmailSent;
        submission.AdminEmailSent = adminEmailSent;
        submission.EmailSendCompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync();
    }

    private static string? NormalizeIpAddress(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        var normalized = ipAddress.Trim();
        return normalized.Length <= 45 ? normalized : normalized[..45];
    }
}