#nullable enable

using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class AdminMessageService : IAdminMessageService
{
    private const int BatchSize = 10;
    private const int DelayBetweenBatchesMs = 60000;
    private const int DelayBetweenEmailsMs = 5000;
    private const int HistoryPreviewLength = 160;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly IEmailService _emailService;
    private readonly IHubContext<AdminMessageHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminMessageService> _logger;

    public AdminMessageService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAdminNotificationService adminNotificationService,
        IEmailService emailService,
        IHubContext<AdminMessageHub> hubContext,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<AdminMessageService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _adminNotificationService = adminNotificationService;
        _emailService = emailService;
        _hubContext = hubContext;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetAvailableRoleNamesAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Roles
            .AsNoTracking()
            .Select(role => role.Name)
            .Where(roleName => !string.IsNullOrWhiteSpace(roleName))
            .OrderBy(roleName => roleName)
            .Select(roleName => roleName!)
            .ToListAsync();
    }

    public async Task<AdminMessageSummaryDto> CreateMessageAsync(CreateAdminMessageRequest request, int createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Subject is required.", nameof(request));
        }

        var messageText = request.MessageText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(messageText))
        {
            throw new ArgumentException("Message text is required.", nameof(request));
        }

        if (!request.SendEmail && !request.ShowDialog)
        {
            throw new ArgumentException("At least one delivery channel must be selected.", nameof(request));
        }

        var selectedRoleNames = NormalizeRoleNames(request.RoleNames);
        if (selectedRoleNames.Count == 0)
        {
            throw new ArgumentException("At least one role must be selected.", nameof(request));
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var validRoleNames = await context.Roles
            .AsNoTracking()
            .Where(role => role.Name != null && selectedRoleNames.Contains(role.Name))
            .Select(role => role.Name!)
            .ToListAsync();

        var missingRoles = selectedRoleNames
            .Except(validRoleNames, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRoles.Count > 0)
        {
            throw new InvalidOperationException($"The following roles are invalid: {string.Join(", ", missingRoles)}");
        }

        var recipientRows = await (
            from user in context.Users
            join userRole in context.UserRoles on user.Id equals userRole.UserId
            join role in context.Roles on userRole.RoleId equals role.Id
            where role.Name != null && validRoleNames.Contains(role.Name)
            select new
            {
                user.Id,
                user.Email
            })
            .ToListAsync();

        var frozenRecipients = recipientRows
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .OrderBy(row => row.Id)
            .ToList();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var message = new AdminMessage
        {
            CreatedByUserId = createdByUserId,
            Subject = subject,
            MessageText = messageText,
            SendEmail = request.SendEmail,
            ShowDialog = request.ShowDialog,
            CreatedAtUtc = now,
            Roles = validRoleNames
                .OrderBy(roleName => roleName)
                .Select(roleName => new AdminMessageRole { RoleName = roleName })
                .ToList(),
            Recipients = frozenRecipients
                .Select(row => new AdminMessageRecipient
                {
                    UserId = row.Id,
                    EmailAddressSnapshot = row.Email ?? string.Empty
                })
                .ToList()
        };

        context.AdminMessages.Add(message);
        await context.SaveChangesAsync();

        if (message.ShowDialog)
        {
            await SendRefreshAsync(message.Recipients.Select(recipient => recipient.UserId));
        }

        _logger.LogInformation(
            "Created admin message {MessageId} for {RecipientCount} frozen recipients across roles {Roles}",
            message.Id,
            message.Recipients.Count,
            string.Join(", ", validRoleNames));

        return MapSummary(message);
    }

    public async Task<IReadOnlyList<AdminMessageSummaryDto>> GetMessagesAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var messages = await context.AdminMessages
            .AsNoTracking()
            .Include(message => message.Roles)
            .Include(message => message.Recipients)
            .OrderByDescending(message => message.CreatedAtUtc)
            .ToListAsync();

        return messages
            .Select(MapSummary)
            .ToList();
    }

    public async Task<IReadOnlyList<PendingAdminMessageDto>> GetPendingDialogMessagesAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var recipients = await context.AdminMessageRecipients
            .Include(recipient => recipient.AdminMessage)
            .Where(recipient => recipient.UserId == userId
                && recipient.AcknowledgedAtUtc == null
                && recipient.CanceledAtUtc == null
                && recipient.AdminMessage.CanceledAtUtc == null
                && recipient.AdminMessage.ShowDialog)
            .OrderBy(recipient => recipient.AdminMessage.CreatedAtUtc)
            .ToListAsync();

        if (recipients.Count == 0)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requiresSave = false;
        foreach (var recipient in recipients.Where(recipient => recipient.DialogDeliveredAtUtc == null))
        {
            recipient.DialogDeliveredAtUtc = now;
            requiresSave = true;
        }

        if (requiresSave)
        {
            await context.SaveChangesAsync();
        }

        return recipients
            .Select(recipient => new PendingAdminMessageDto
            {
                MessageId = recipient.AdminMessageId,
                Subject = recipient.AdminMessage.Subject,
                MessageText = recipient.AdminMessage.MessageText,
                CreatedAtUtc = recipient.AdminMessage.CreatedAtUtc
            })
            .ToList();
    }

    public async Task<bool> AcknowledgeMessageAsync(int userId, int messageId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var recipient = await context.AdminMessageRecipients
            .Include(row => row.AdminMessage)
            .Include(row => row.User)
            .FirstOrDefaultAsync(row => row.UserId == userId && row.AdminMessageId == messageId);

        if (recipient == null || recipient.CanceledAtUtc != null)
        {
            return false;
        }

        if (recipient.AcknowledgedAtUtc != null)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        recipient.AcknowledgedAtUtc = now;
        recipient.DialogDeliveredAtUtc ??= now;
        await context.SaveChangesAsync();

        var email = recipient.User.Email ?? recipient.EmailAddressSnapshot;
        await _adminNotificationService.RecordUserHistoryAsync(
            userId,
            email,
            UserHistoryEventTypes.AdminMessageAcknowledged,
            $"User acknowledged admin message #{messageId}: {CreatePreview(recipient.AdminMessage.MessageText)}");

        if (recipient.AdminMessage.ShowDialog)
        {
            await SendRefreshAsync([userId]);
        }

        _logger.LogInformation("User {UserId} acknowledged admin message {MessageId}", userId, messageId);
        return true;
    }

    public async Task<int> CancelMessageAsync(int messageId, int canceledByUserId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var message = await context.AdminMessages
            .Include(row => row.Recipients)
            .FirstOrDefaultAsync(row => row.Id == messageId);

        if (message == null)
        {
            throw new InvalidOperationException($"Admin message {messageId} was not found.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var pendingRecipients = message.Recipients
            .Where(recipient => recipient.AcknowledgedAtUtc == null && recipient.CanceledAtUtc == null)
            .ToList();

        foreach (var recipient in pendingRecipients)
        {
            recipient.CanceledAtUtc = now;
        }

        message.CanceledAtUtc ??= now;
        message.CanceledByUserId ??= canceledByUserId;
        await context.SaveChangesAsync();

        if (message.ShowDialog && pendingRecipients.Count > 0)
        {
            await SendRefreshAsync(pendingRecipients.Select(recipient => recipient.UserId));
        }

        _logger.LogInformation(
            "Canceled admin message {MessageId} for {RecipientCount} unacknowledged recipients",
            messageId,
            pendingRecipients.Count);

        return pendingRecipients.Count;
    }

    public async Task<AdminMessageEmailJobResult> SendPendingEmailsAsync()
    {
        _logger.LogInformation("Starting nightly admin message email job");

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var pendingRecipients = await context.AdminMessageRecipients
            .Include(recipient => recipient.AdminMessage)
            .Include(recipient => recipient.User)
            .Where(recipient => recipient.EmailSentAtUtc == null
                && recipient.AcknowledgedAtUtc == null
                && recipient.CanceledAtUtc == null
                && recipient.AdminMessage.CanceledAtUtc == null
                && recipient.AdminMessage.SendEmail)
            .OrderBy(recipient => recipient.AdminMessage.CreatedAtUtc)
            .ThenBy(recipient => recipient.UserId)
            .ToListAsync();

        var result = new AdminMessageEmailJobResult
        {
            ConsideredCount = pendingRecipients.Count
        };

        if (pendingRecipients.Count == 0)
        {
            _logger.LogInformation("No pending admin message emails found.");
            return result;
        }

        for (var offset = 0; offset < pendingRecipients.Count; offset += BatchSize)
        {
            var batch = pendingRecipients.Skip(offset).Take(BatchSize).ToList();
            _logger.LogInformation(
                "Processing admin message email batch {BatchNumber} with {Count} recipients",
                (offset / BatchSize) + 1,
                batch.Count);

            foreach (var recipient in batch)
            {
                if (!CanSendEmail(recipient, out var toEmail))
                {
                    result.SkippedCount++;
                    continue;
                }

                var subject = recipient.AdminMessage.Subject;
                var body = BuildRecipientEmailBody(recipient.User, recipient.AdminMessage.Subject, recipient.AdminMessage.MessageText);

                try
                {
                    var sent = await _emailService.SendEmailAsync(toEmail, subject, body);
                    if (sent)
                    {
                        recipient.EmailSentAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                        await context.SaveChangesAsync();
                        result.SentCount++;
                    }
                    else
                    {
                        result.FailedCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    _logger.LogError(ex,
                        "Failed to send admin message {MessageId} email to user {UserId}",
                        recipient.AdminMessageId,
                        recipient.UserId);
                }

                await Task.Delay(DelayBetweenEmailsMs);
            }

            if (offset + BatchSize < pendingRecipients.Count)
            {
                _logger.LogInformation(
                    "Waiting {DelaySeconds} seconds before the next admin message email batch",
                    DelayBetweenBatchesMs / 1000);
                await Task.Delay(DelayBetweenBatchesMs);
            }
        }

        _logger.LogInformation(
            "Completed nightly admin message email job. Considered={ConsideredCount}, Sent={SentCount}, Failed={FailedCount}, Skipped={SkippedCount}",
            result.ConsideredCount,
            result.SentCount,
            result.FailedCount,
            result.SkippedCount);

        return result;
    }

    private static List<string> NormalizeRoleNames(IEnumerable<string>? roleNames)
    {
        return (roleNames ?? [])
            .Where(roleName => !string.IsNullOrWhiteSpace(roleName))
            .Select(roleName => roleName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AdminMessageSummaryDto MapSummary(AdminMessage message)
    {
        var acknowledgedCount = message.Recipients.Count(recipient => recipient.AcknowledgedAtUtc != null);
        var canceledCount = message.Recipients.Count(recipient => recipient.CanceledAtUtc != null);

        return new AdminMessageSummaryDto
        {
            Id = message.Id,
            Subject = message.Subject,
            MessageText = message.MessageText,
            RoleNames = message.Roles
                .Select(role => role.RoleName)
                .OrderBy(roleName => roleName)
                .ToList(),
            SendEmail = message.SendEmail,
            ShowDialog = message.ShowDialog,
            CreatedAtUtc = message.CreatedAtUtc,
            CanceledAtUtc = message.CanceledAtUtc,
            RecipientCount = message.Recipients.Count,
            AcknowledgedCount = acknowledgedCount,
            PendingCount = message.Recipients.Count - acknowledgedCount - canceledCount,
            EmailedCount = message.Recipients.Count(recipient => recipient.EmailSentAtUtc != null),
            CanceledCount = canceledCount
        };
    }

    private bool CanSendEmail(AdminMessageRecipient recipient, out string toEmail)
    {
        toEmail = string.Empty;

        if (recipient.User.IsSuspended || !recipient.User.EmailConfirmed)
        {
            return false;
        }

        toEmail = string.IsNullOrWhiteSpace(recipient.User.Email)
            ? recipient.EmailAddressSnapshot
            : recipient.User.Email!;

        return !string.IsNullOrWhiteSpace(toEmail);
    }

    private string BuildRecipientEmailBody(ApplicationUser user, string subject, string messageText)
    {
        var greetingName = GetGreetingName(user);
        var customerServiceEmail = _configuration["EmailSettings:CustomerServiceEmail"] ?? "admin@streamtunes.net";
        var messageHtml = BuildParagraphHtml(messageText);
        var encodedSubject = WebUtility.HtmlEncode(subject);

        return $@"
{_emailService.GetEmailLogoHtml()}
<p><strong>Message from StreamTunes Customer Service</strong></p>
<h2>{encodedSubject}</h2>
<p>Hello {WebUtility.HtmlEncode(greetingName)},</p>
{messageHtml}
<p>If you need help, reply to <a href='mailto:{WebUtility.HtmlEncode(customerServiceEmail)}'>{WebUtility.HtmlEncode(customerServiceEmail)}</a>.</p>
<p>Thank you,<br />StreamTunes Customer Service</p>";
    }

    private static string BuildParagraphHtml(string messageText)
    {
        var normalized = messageText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var paragraphs = normalized
            .Split("\n\n", StringSplitOptions.None)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
        {
            return "<p></p>";
        }

        return string.Join(string.Empty, paragraphs.Select(paragraph =>
            $"<p>{WebUtility.HtmlEncode(paragraph).Replace("\n", "<br />", StringComparison.Ordinal)}</p>"));
    }

    private static string GetGreetingName(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.UserName))
        {
            return user.UserName;
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            var atIndex = user.Email.IndexOf('@');
            return atIndex > 0 ? user.Email[..atIndex] : user.Email;
        }

        return "StreamTunes listener";
    }

    private static string CreatePreview(string messageText)
    {
        var normalized = string.Join(" ", messageText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length <= HistoryPreviewLength)
        {
            return normalized;
        }

        return normalized[..HistoryPreviewLength] + "...";
    }

    private Task SendRefreshAsync(IEnumerable<int> userIds)
    {
        var distinctUserIds = userIds
            .Distinct()
            .Select(userId => _hubContext.Clients.User(userId.ToString())
                .SendAsync(SignalRMethodNames.ReceiveAdminMessageRefresh))
            .ToList();

        return distinctUserIds.Count == 0
            ? Task.CompletedTask
            : Task.WhenAll(distinctUserIds);
    }
}