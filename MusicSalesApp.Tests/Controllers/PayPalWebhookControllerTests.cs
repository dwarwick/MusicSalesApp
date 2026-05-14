#nullable enable
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class PayPalWebhookControllerTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockDbContextFactory;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IWebHostEnvironment> _mockEnvironment;
    private Mock<ILogger<PayPalWebhookController>> _mockLogger;
    private PayPalWebhookController _controller;
    private DbContextOptions<AppDbContext> _dbOptions;

    [SetUp]
    public void SetUp()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _mockDbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockAdminNotificationService.Setup(x => x.IsNotificationEnabledAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService.Setup(x => x.GetLogoUrl())
            .Returns("https://streamtunes.net/images/logo-light-small.png");
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockConfiguration = new Mock<IConfiguration>();
        // Use placeholder webhook ID to skip signature verification in tests
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns("REPLACE_ME");
        _mockConfiguration.Setup(c => c["PayPal:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["PayPal:Secret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["PayPal:ApiBaseUrl"]).Returns("https://api-m.sandbox.paypal.com/");
        _mockConfiguration.Setup(c => c["BaseUrl"]).Returns("https://davidtest.dev");

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        // Set up environment as Development so placeholder WebhookId is accepted
        _mockEnvironment = new Mock<IWebHostEnvironment>();
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Development);

        _mockLogger = new Mock<ILogger<PayPalWebhookController>>();

        _controller = new PayPalWebhookController(
            _mockDbContextFactory.Object,
            _mockSubscriptionService.Object,
            _mockAdminNotificationService.Object,
            _mockAccountEmailService.Object,
            _mockEmailService.Object,
            _mockConfiguration.Object,
            _mockHttpClientFactory.Object,
            _mockUserManager.Object,
            _mockEnvironment.Object,
            _mockLogger.Object);
    }

    private void SetRequestBody(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = body.Length;
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };
    }

    [Test]
    public async Task HandleWebhook_ReturnsBadRequest_WhenBodyIsEmpty()
    {
        // Arrange
        SetRequestBody("");

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleWebhook_ReturnsBadRequest_WhenBodyIsInvalidJson()
    {
        // Arrange
        SetRequestBody("not valid json {{{");

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleWebhook_ReturnsOk_WhenEventTypeIsUnrecognized()
    {
        // Arrange
        var body = @"{""event_type"":""PAYMENT.SALE.COMPLETED"",""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_BillingSubscriptionCancelled_UpdatesSubscriptionStatus()
    {
        var subscription = new Subscription
        {
            Id = 15,
            UserId = 7,
            EndDate = new DateTime(2026, 5, 20, 12, 34, 56, DateTimeKind.Utc)
        };
        var user = new ApplicationUser
        {
            Id = 7,
            UserName = "paypaluser",
            Email = "paypal@example.com"
        };

        _mockSubscriptionService.Setup(s => s.GetSubscriptionByPayPalIdAsync("I-SUB123"))
            .ReturnsAsync(subscription);
        _mockUserManager.Setup(m => m.FindByIdAsync("7"))
            .ReturnsAsync(user);

        var body = @"{
            ""event_type"": ""BILLING.SUBSCRIPTION.CANCELLED"",
            ""resource"": {
                ""id"": ""I-SUB123"",
                ""billing_info"": {
                    ""next_billing_time"": ""2026-05-20T12:34:56Z""
                }
            }
        }";
        SetRequestBody(body);

        var result = await _controller.HandleWebhook();

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(s => s.UpdateSubscriptionStatusAsync(
            "I-SUB123",
            SubscriptionStatuses.Cancelled,
            It.Is<DateTime?>(value => value == new DateTime(2026, 5, 20, 12, 34, 56, DateTimeKind.Utc))), Times.Once);
        _mockAccountEmailService.Verify(service => service.SendSubscriptionCancelledEmailAsync(
            "paypal@example.com",
            "paypaluser",
            subscription.EndDate,
            BillingSources.PayPal,
            null,
            "https://davidtest.dev"), Times.Once);
    }

    [Test]
    public async Task HandleWebhook_BillingSubscriptionUpdated_UsesResourceStatus()
    {
        var body = @"{
            ""event_type"": ""BILLING.SUBSCRIPTION.UPDATED"",
            ""resource"": {
                ""id"": ""I-SUB123"",
                ""status"": ""ACTIVE"",
                ""billing_info"": {
                    ""next_billing_time"": ""2026-05-20T12:34:56Z""
                }
            }
        }";
        SetRequestBody(body);

        var result = await _controller.HandleWebhook();

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(s => s.UpdateSubscriptionStatusAsync(
            "I-SUB123",
            SubscriptionStatuses.Active,
            It.Is<DateTime?>(value => value == new DateTime(2026, 5, 20, 12, 34, 56, DateTimeKind.Utc))), Times.Once);
        _mockAccountEmailService.Verify(service => service.SendSubscriptionCancelledEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task HandleWebhook_ReturnsOk_WhenDisputeCreatedWithNoTransactions()
    {
        // Arrange - dispute with no disputed_transactions
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-11111"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""3.99"", ""currency_code"": ""USD"" }
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify ChargebackLog was created with NO_TRANSACTION_FOUND status
        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.PayPalDisputeId, Is.EqualTo("PP-D-11111"));
        Assert.That(log.Status, Is.EqualTo("NO_TRANSACTION_FOUND"));
        Assert.That(log.Reason, Is.EqualTo("UNAUTHORISED"));
        Assert.That(log.Stage, Is.EqualTo("CHARGEBACK"));
        Assert.That(log.Channel, Is.EqualTo("EXTERNAL"));
        Assert.That(log.Amount, Is.EqualTo("3.99 USD"));
    }

    [Test]
    public async Task HandleWebhook_ReturnsOk_WhenDisputeCreatedWithNoMatchingTransaction()
    {
        // Arrange - dispute with a transaction that matches nothing
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-22222"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""dispute_life_cycle_stage"": ""INQUIRY"",
                ""dispute_channel"": ""INTERNAL"",
                ""dispute_amount"": { ""value"": ""5.00"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""NONEXISTENT-TXN"" }
                ]
            }
        }";
        SetRequestBody(body);

        // PayPal sale lookup will fail (no HttpClient configured) - falls through to tip check
        // No tip found - logs NO_TRANSACTION_FOUND

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Status, Is.EqualTo("NO_TRANSACTION_FOUND"));
        Assert.That(log.SellerTransactionId, Is.EqualTo("NONEXISTENT-TXN"));
    }

    [Test]
    public async Task HandleWebhook_ProcessesTipChargeback_WhenTipMatches()
    {
        // Arrange - create a tip with PayPalCaptureId
        using (var context = new AppDbContext(_dbOptions))
        {
            // Need to seed a user for FK
            context.Users.Add(new ApplicationUser
            {
                Id = 10,
                UserName = "tipper@example.com",
                NormalizedUserName = "TIPPER@EXAMPLE.COM",
                Email = "tipper@example.com",
                NormalizedEmail = "TIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            context.Users.Add(new ApplicationUser
            {
                Id = 11,
                UserName = "creator@example.com",
                NormalizedUserName = "CREATOR@EXAMPLE.COM",
                Email = "creator@example.com",
                NormalizedEmail = "CREATOR@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            context.Creators.Add(new Creator
            {
                Id = 1,
                UserId = 11,
                DisplayName = "Test Creator"
            });

            context.Tips.Add(new Tip
            {
                Id = 1,
                TipperUserId = 10,
                CreatorId = 1,
                Amount = 5.00m,
                Status = TipStatus.Cleared,
                PayPalOrderId = "ORDER-123",
                PayPalCaptureId = "CAPTURE-TXN-456",
                CapturedAt = DateTime.UtcNow.AddDays(-2),
                CreatedAt = DateTime.UtcNow.AddDays(-2)
            });

            await context.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-33333"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""5.00"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-TXN-456"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify tip status changed to Chargeback
        using (var context = new AppDbContext(_dbOptions))
        {
            var tip = await context.Tips.FirstAsync(t => t.Id == 1);
            Assert.That(tip.Status, Is.EqualTo(TipStatus.Chargeback));
        }

        // Verify ChargebackLog was created with PROCESSED_TIP status
        using (var context = new AppDbContext(_dbOptions))
        {
            var log = await context.ChargebackLogs.FirstOrDefaultAsync();
            Assert.That(log, Is.Not.Null);
            Assert.That(log.PayPalDisputeId, Is.EqualTo("PP-D-33333"));
            Assert.That(log.Status, Is.EqualTo("PROCESSED_TIP"));
            Assert.That(log.TipId, Is.EqualTo(1));
            Assert.That(log.SellerTransactionId, Is.EqualTo("CAPTURE-TXN-456"));
            Assert.That(log.UserEmail, Is.EqualTo("tipper@example.com"));
        }

        // Verify admin notification was sent
        _mockAdminNotificationService.Verify(
            x => x.IsNotificationEnabledAsync(AdminNotificationService.NotifyChargebackReceivedKey),
            Times.Once);
    }

    [Test]
    public async Task HandleWebhook_TipChargeback_WarnsWhenAlreadyPaid()
    {
        // Arrange - create a tip that was already paid out
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Users.Add(new ApplicationUser
            {
                Id = 10,
                UserName = "tipper@example.com",
                NormalizedUserName = "TIPPER@EXAMPLE.COM",
                Email = "tipper@example.com",
                NormalizedEmail = "TIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            context.Creators.Add(new Creator
            {
                Id = 1,
                UserId = 10,
                DisplayName = "Test Creator"
            });

            context.Tips.Add(new Tip
            {
                Id = 1,
                TipperUserId = 10,
                CreatorId = 1,
                Amount = 10.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = "ORDER-PAID",
                PayPalCaptureId = "CAPTURE-PAID-789",
                CapturedAt = DateTime.UtcNow.AddDays(-14),
                PaidAt = DateTime.UtcNow.AddDays(-5),
                CreatedAt = DateTime.UtcNow.AddDays(-14)
            });

            await context.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-44444"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""10.00"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-PAID-789"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify the chargeback log notes contain the payout warning
        using var context2 = new AppDbContext(_dbOptions);
        var log = await context2.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Status, Is.EqualTo("PROCESSED_TIP"));
        Assert.That(log.Notes, Does.Contain("already paid out"));
        Assert.That(log.Notes, Does.Contain("payout reversal"));
    }

    [Test]
    public async Task HandleWebhook_VerifiesSignature_WhenWebhookIdIsConfigured()
    {
        // Arrange - configure a real webhook ID (not placeholder)
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns("WH-REAL-WEBHOOK-ID");

        var body = @"{""event_type"":""CUSTOMER.DISPUTE.CREATED"",""resource"":{}}";
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = body.Length;
        // No signature headers provided - should fail verification
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        // Act
        var result = await _controller.HandleWebhook();

        // Assert - should return 401 because signature verification fails
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task HandleWebhook_SkipsSignatureVerification_WhenWebhookIdIsPlaceholder()
    {
        // Arrange - placeholder webhook ID should skip verification
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns("REPLACE_ME");

        var body = @"{""event_type"":""PAYMENT.SALE.COMPLETED"",""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert - should return 200 OK (no verification needed)
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_SkipsSignatureVerification_WhenWebhookIdIsEmpty()
    {
        // Arrange - empty webhook ID should skip verification
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns(string.Empty);

        var body = @"{""event_type"":""PAYMENT.SALE.COMPLETED"",""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_ExtractsDisputeDetailsCorrectly()
    {
        // Arrange - full dispute payload with all details
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-55555"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""dispute_life_cycle_stage"": ""INQUIRY"",
                ""dispute_channel"": ""INTERNAL"",
                ""dispute_amount"": { ""value"": ""25.99"", ""currency_code"": ""EUR"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""UNKNOWN-TXN-999"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.PayPalDisputeId, Is.EqualTo("PP-D-55555"));
        Assert.That(log.Reason, Is.EqualTo("MERCHANDISE_OR_SERVICE_NOT_RECEIVED"));
        Assert.That(log.Stage, Is.EqualTo("INQUIRY"));
        Assert.That(log.Channel, Is.EqualTo("INTERNAL"));
        Assert.That(log.Amount, Is.EqualTo("25.99 EUR"));
        Assert.That(log.SellerTransactionId, Is.EqualTo("UNKNOWN-TXN-999"));
    }

    [Test]
    public async Task HandleWebhook_SuppressesAdminNotification_WhenDisabled()
    {
        // Arrange - disable chargeback notifications
        _mockAdminNotificationService.Setup(x =>
            x.IsNotificationEnabledAsync(AdminNotificationService.NotifyChargebackReceivedKey))
            .ReturnsAsync(false);

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-66666"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""UNKNOWN-TXN"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify email was NOT sent for admin notification
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Chargeback")),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task HandleWebhook_MultipleDuplicateDisputes_CreateSeparateLogs()
    {
        // Arrange - same transaction but different dispute IDs
        var body1 = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-FIRST"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SAME"" }
                ]
            }
        }";

        var body2 = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-SECOND"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SAME"" }
                ]
            }
        }";

        // Process first dispute
        SetRequestBody(body1);
        await _controller.HandleWebhook();

        // Process second dispute
        SetRequestBody(body2);
        await _controller.HandleWebhook();

        // Assert - both should create separate log entries
        using var context = new AppDbContext(_dbOptions);
        var logs = await context.ChargebackLogs.ToListAsync();
        Assert.That(logs, Has.Count.EqualTo(2));
        Assert.That(logs.Select(l => l.PayPalDisputeId), Does.Contain("PP-D-FIRST"));
        Assert.That(logs.Select(l => l.PayPalDisputeId), Does.Contain("PP-D-SECOND"));
    }
}
