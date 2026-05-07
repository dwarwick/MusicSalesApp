#nullable enable

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AdminMessageServiceTests
{
    private Mock<IAdminNotificationService> _mockAdminNotificationService = default!;
    private Mock<IEmailService> _mockEmailService = default!;
    private Mock<IHubContext<AdminMessageHub>> _mockHubContext = default!;
    private Mock<IHubClients> _mockHubClients = default!;
    private Mock<IClientProxy> _mockClientProxy = default!;
    private Mock<ILogger<AdminMessageService>> _mockLogger = default!;
    private IConfiguration _configuration = default!;
    private IDbContextFactory<AppDbContext> _contextFactory = default!;
    private DbContextOptions<AppDbContext> _dbOptions = default!;
    private AdminMessageService _service = default!;

    [SetUp]
    public void SetUp()
    {
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockEmailService = new Mock<IEmailService>();
        _mockHubContext = new Mock<IHubContext<AdminMessageHub>>();
        _mockHubClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockLogger = new Mock<ILogger<AdminMessageService>>();

        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockEmailService.Setup(x => x.GetEmailLogoHtml())
            .Returns("<div>Logo</div>");

        _mockHubContext.SetupGet(x => x.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(x => x.User(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockClientProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default))
            .Returns(Task.CompletedTask);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:CustomerServiceEmail"] = "customerservice@streamtunes.net"
            })
            .Build();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AdminMessageTests_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(_dbOptions);
        SeedBaseData();

        _service = new AdminMessageService(
            _contextFactory,
            _mockAdminNotificationService.Object,
            _mockEmailService.Object,
            _mockHubContext.Object,
            _configuration,
            TimeProvider.System,
            _mockLogger.Object);
    }

    [Test]
    public async Task CreateMessageAsync_FreezesDistinctRecipientsAcrossSelectedRoles()
    {
        var result = await _service.CreateMessageAsync(new CreateAdminMessageRequest
        {
            Subject = "Creator notice",
            MessageText = "Hello creators and users",
            RoleNames = new[] { Roles.User, Roles.Creator },
            ShowDialog = true,
            SendEmail = false
        }, createdByUserId: 1);

        Assert.That(result.RecipientCount, Is.EqualTo(2));
        Assert.That(result.RoleNames, Is.EquivalentTo(new[] { Roles.Creator, Roles.User }));
        Assert.That(result.Subject, Is.EqualTo("Creator notice"));

        await using var context = new AppDbContext(_dbOptions);
        var message = await context.AdminMessages
            .Include(row => row.Roles)
            .Include(row => row.Recipients)
            .SingleAsync();

        Assert.That(message.Recipients.Select(row => row.UserId), Is.EquivalentTo(new[] { 2, 3 }));
        _mockHubClients.Verify(x => x.User("2"), Times.Once);
        _mockHubClients.Verify(x => x.User("3"), Times.Once);
    }

    [Test]
    public async Task CreateMessageAsync_TargetingSingleRole_IncludesMultiRoleUserOnce()
    {
        var result = await _service.CreateMessageAsync(new CreateAdminMessageRequest
        {
            Subject = "User notice",
            MessageText = "Hello users",
            RoleNames = new[] { Roles.User },
            ShowDialog = true,
            SendEmail = false
        }, createdByUserId: 1);

        Assert.That(result.RecipientCount, Is.EqualTo(2));
        Assert.That(result.RoleNames, Is.EquivalentTo(new[] { Roles.User }));

        await using var context = new AppDbContext(_dbOptions);
        var message = await context.AdminMessages
            .Include(row => row.Roles)
            .Include(row => row.Recipients)
            .SingleAsync(row => row.Subject == "User notice");

        Assert.That(message.Recipients.Select(row => row.UserId), Is.EquivalentTo(new[] { 2, 3 }));
        Assert.That(message.Recipients.Count(row => row.UserId == 3), Is.EqualTo(1));
    }

    [Test]
    public async Task GetPendingDialogMessagesAsync_ReturnsOldestFirst_AndMarksDelivered()
    {
        await SeedPendingMessageAsync(2, "First", DateTime.UtcNow.AddDays(-2));
        await SeedPendingMessageAsync(2, "Second", DateTime.UtcNow.AddDays(-1));

        var messages = await _service.GetPendingDialogMessagesAsync(2);

        Assert.That(messages.Select(message => message.MessageText), Is.EqualTo(new[] { "First", "Second" }));
        Assert.That(messages.Select(message => message.Subject), Is.EqualTo(new[] { "Admin subject", "Admin subject" }));

        await using var context = new AppDbContext(_dbOptions);
        Assert.That(await context.AdminMessageRecipients.AllAsync(row => row.DialogDeliveredAtUtc != null), Is.True);
    }

    [Test]
    public async Task AcknowledgeMessageAsync_RecordsUserHistory()
    {
        var messageId = await SeedPendingMessageAsync(2, "Acknowledge me", DateTime.UtcNow);

        var acknowledged = await _service.AcknowledgeMessageAsync(2, messageId);

        Assert.That(acknowledged, Is.True);
        _mockAdminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            2,
            "listener@example.com",
            UserHistoryEventTypes.AdminMessageAcknowledged,
            It.Is<string>(text => text.Contains($"#{messageId}")),
            null,
            null), Times.Once);
        _mockHubClients.Verify(x => x.User("2"), Times.AtLeastOnce);
    }

    [Test]
    public async Task CancelMessageAsync_OnlyCancelsUnacknowledgedRecipients()
    {
        var messageId = await SeedPendingMessageAsync(2, "Cancel pending only", DateTime.UtcNow);
        await SeedAdditionalRecipientAsync(messageId, 3, acknowledgedAtUtc: DateTime.UtcNow);

        var canceledCount = await _service.CancelMessageAsync(messageId, canceledByUserId: 1);

        Assert.That(canceledCount, Is.EqualTo(1));

        await using var context = new AppDbContext(_dbOptions);
        var recipients = await context.AdminMessageRecipients
            .Where(row => row.AdminMessageId == messageId)
            .OrderBy(row => row.UserId)
            .ToListAsync();

        Assert.That(recipients[0].CanceledAtUtc, Is.Not.Null);
        Assert.That(recipients[1].CanceledAtUtc, Is.Null);
    }

    [Test]
    public async Task SendPendingEmailsAsync_SendsOnceToEligibleRecipients()
    {
        var messageId = await SeedPendingMessageAsync(2, "Email this", DateTime.UtcNow, sendEmail: true, showDialog: false);
        await SeedAdditionalRecipientAsync(messageId, 3, acknowledgedAtUtc: DateTime.UtcNow);

        var result = await _service.SendPendingEmailsAsync();

        Assert.That(result.SentCount, Is.EqualTo(1));
        Assert.That(result.SkippedCount, Is.EqualTo(0));

        _mockEmailService.Verify(x => x.SendEmailAsync(
            "listener@example.com",
            It.Is<string>(subject => subject == "Admin subject"),
            It.Is<string>(body => body.Contains("Email this"))), Times.Once);

        await using var context = new AppDbContext(_dbOptions);
        var emailedRecipient = await context.AdminMessageRecipients.SingleAsync(row => row.AdminMessageId == messageId && row.UserId == 2);
        Assert.That(emailedRecipient.EmailSentAtUtc, Is.Not.Null);
    }

    private void SeedBaseData()
    {
        using var context = new AppDbContext(_dbOptions);

        context.Roles.AddRange(
            new Microsoft.AspNetCore.Identity.IdentityRole<int> { Id = 1, Name = Roles.Admin, NormalizedName = Roles.Admin.ToUpperInvariant() },
            new Microsoft.AspNetCore.Identity.IdentityRole<int> { Id = 2, Name = Roles.User, NormalizedName = Roles.User.ToUpperInvariant() },
            new Microsoft.AspNetCore.Identity.IdentityRole<int> { Id = 3, Name = Roles.Creator, NormalizedName = Roles.Creator.ToUpperInvariant() });

        context.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "admin@example.com", NormalizedUserName = "ADMIN@EXAMPLE.COM", Email = "admin@example.com", NormalizedEmail = "ADMIN@EXAMPLE.COM", EmailConfirmed = true },
            new ApplicationUser { Id = 2, UserName = "listener@example.com", NormalizedUserName = "LISTENER@EXAMPLE.COM", Email = "listener@example.com", NormalizedEmail = "LISTENER@EXAMPLE.COM", EmailConfirmed = true },
            new ApplicationUser { Id = 3, UserName = "creator@example.com", NormalizedUserName = "CREATOR@EXAMPLE.COM", Email = "creator@example.com", NormalizedEmail = "CREATOR@EXAMPLE.COM", EmailConfirmed = true });

        context.UserRoles.AddRange(
            new Microsoft.AspNetCore.Identity.IdentityUserRole<int> { UserId = 1, RoleId = 1 },
            new Microsoft.AspNetCore.Identity.IdentityUserRole<int> { UserId = 2, RoleId = 2 },
            new Microsoft.AspNetCore.Identity.IdentityUserRole<int> { UserId = 3, RoleId = 2 },
            new Microsoft.AspNetCore.Identity.IdentityUserRole<int> { UserId = 3, RoleId = 3 });

        context.SaveChanges();
    }

    private async Task<int> SeedPendingMessageAsync(int userId, string text, DateTime createdAtUtc, bool sendEmail = false, bool showDialog = true)
    {
        await using var context = new AppDbContext(_dbOptions);
        var message = new AdminMessage
        {
            CreatedByUserId = 1,
            Subject = "Admin subject",
            MessageText = text,
            SendEmail = sendEmail,
            ShowDialog = showDialog,
            CreatedAtUtc = createdAtUtc,
            Roles = [new AdminMessageRole { RoleName = Roles.User }],
            Recipients = [new AdminMessageRecipient { UserId = userId, EmailAddressSnapshot = userId == 2 ? "listener@example.com" : "creator@example.com" }]
        };

        context.AdminMessages.Add(message);
        await context.SaveChangesAsync();
        return message.Id;
    }

    private async Task SeedAdditionalRecipientAsync(int messageId, int userId, DateTime? acknowledgedAtUtc = null)
    {
        await using var context = new AppDbContext(_dbOptions);
        context.AdminMessageRecipients.Add(new AdminMessageRecipient
        {
            AdminMessageId = messageId,
            UserId = userId,
            EmailAddressSnapshot = userId == 2 ? "listener@example.com" : "creator@example.com",
            AcknowledgedAtUtc = acknowledgedAtUtc
        });

        await context.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }
}