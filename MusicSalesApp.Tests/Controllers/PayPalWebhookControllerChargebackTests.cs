using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

/// <summary>
/// Comprehensive chargeback tests for PayPalWebhookController covering:
/// - Subscription chargeback blocking
/// - Tip chargeback with creator email for manual reversal
/// - Various edge cases and error scenarios
/// </summary>
[TestFixture]
public class PayPalWebhookControllerChargebackTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockDbContextFactory;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<ILogger<PayPalWebhookController>> _mockLogger;
    private PayPalWebhookController _controller;
    private DbContextOptions<AppDbContext> _dbOptions;

    [SetUp]
    public void SetUp()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"ChargebackTestDb_{Guid.NewGuid()}")
            .Options;

        _mockDbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockAdminNotificationService.Setup(x => x.IsNotificationEnabledAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService.Setup(x => x.GetLogoUrl())
            .Returns("https://streamtunes.net/images/logo-light-small.png");
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns("REPLACE_ME");
        _mockConfiguration.Setup(c => c["PayPal:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["PayPal:Secret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["PayPal:ApiBaseUrl"]).Returns("https://api-m.sandbox.paypal.com/");

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null, null, null, null, null, null, null, null);

        _mockLogger = new Mock<ILogger<PayPalWebhookController>>();

        _controller = new PayPalWebhookController(
            _mockDbContextFactory.Object,
            _mockSubscriptionService.Object,
            _mockAdminNotificationService.Object,
            _mockEmailService.Object,
            _mockConfiguration.Object,
            _mockHttpClientFactory.Object,
            _mockUserManager.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureDeleted();
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

    #region Subscription Chargeback - User Blocking

    [Test]
    public async Task SubscriptionChargeback_BlocksUserFromCreatingNewSubscriptions()
    {
        // Arrange - Create a user with an active subscription
        var userId = 100;
        var paypalSubId = "I-SUB-BLOCK-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "subscriber@example.com",
                NormalizedUserName = "SUBSCRIBER@EXAMPLE.COM",
                Email = "subscriber@example.com",
                NormalizedEmail = "SUBSCRIBER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                IsSubscriptionBlocked = false
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 1,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m,
                StartDate = DateTime.UtcNow.AddMonths(-1)
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 1,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        // Mock HttpClient to return billing_agreement_id for subscription lookup
        var mockHandler = CreateMockHttpHandler(
            $"v1/payments/sale/TXN-SUB-BLOCK",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");

        // Also mock the refund endpoint
        AddMockHttpResponse(mockHandler,
            "v2/payments/captures/TXN-SUB-BLOCK/refund",
            System.Net.HttpStatusCode.OK,
            "{}");

        // Mock the subscription cancel endpoint
        AddMockHttpResponse(mockHandler,
            $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent,
            "");

        // Mock token endpoint
        AddMockHttpResponse(mockHandler,
            "v1/oauth2/token",
            System.Net.HttpStatusCode.OK,
            "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-BLOCK-001"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""3.99"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SUB-BLOCK"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify user is now blocked
        using (var ctx = new AppDbContext(_dbOptions))
        {
            var user = await ctx.Users.FirstAsync(u => u.Id == userId);
            Assert.That(user.IsSubscriptionBlocked, Is.True,
                "User should be blocked from creating new subscriptions after chargeback");
            Assert.That(user.SubscriptionBlockedAt, Is.Not.Null,
                "SubscriptionBlockedAt should be set");
            Assert.That(user.SubscriptionBlockedAt!.Value, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(10)),
                "SubscriptionBlockedAt should be approximately now");
        }

        // Verify subscription was cancelled
        _mockSubscriptionService.Verify(
            x => x.UpdateSubscriptionStatusAsync(paypalSubId, "CANCELLED", null),
            Times.Once);
    }

    [Test]
    public async Task SubscriptionChargeback_CreatesChargebackLog_WithSubscriptionDetails()
    {
        // Arrange
        var userId = 101;
        var paypalSubId = "I-SUB-LOG-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "loguser@example.com",
                NormalizedUserName = "LOGUSER@EXAMPLE.COM",
                Email = "loguser@example.com",
                NormalizedEmail = "LOGUSER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 2,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 2,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-SUB-LOG",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-SUB-LOG/refund",
            System.Net.HttpStatusCode.OK, "{}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent, "");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-LOG-001"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""3.99"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SUB-LOG"" }
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
        Assert.That(log.PayPalDisputeId, Is.EqualTo("PP-D-LOG-001"));
        Assert.That(log.Status, Is.EqualTo("PROCESSED_SUBSCRIPTION"));
        Assert.That(log.PayPalSubscriptionId, Is.EqualTo(paypalSubId));
        Assert.That(log.UserId, Is.EqualTo(userId));
        Assert.That(log.UserEmail, Is.EqualTo("loguser@example.com"));
        Assert.That(log.SellerTransactionId, Is.EqualTo("TXN-SUB-LOG"));
        Assert.That(log.Reason, Is.EqualTo("UNAUTHORISED"));
        Assert.That(log.Stage, Is.EqualTo("CHARGEBACK"));
        Assert.That(log.Channel, Is.EqualTo("EXTERNAL"));
        Assert.That(log.Amount, Is.EqualTo("3.99 USD"));
    }

    [Test]
    public async Task SubscriptionChargeback_SendsSubscriberEmail()
    {
        // Arrange
        var userId = 102;
        var paypalSubId = "I-SUB-EMAIL-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "emailuser@example.com",
                NormalizedUserName = "EMAILUSER@EXAMPLE.COM",
                Email = "emailuser@example.com",
                NormalizedEmail = "EMAILUSER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 3,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 3,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-SUB-EMAIL",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-SUB-EMAIL/refund",
            System.Net.HttpStatusCode.OK, "{}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent, "");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-EMAIL-001"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SUB-EMAIL"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - verify subscriber email was sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "emailuser@example.com",
                It.Is<string>(s => s.Contains("Subscription Cancelled")),
                It.Is<string>(b => b.Contains("chargeback"))),
            Times.Once);
    }

    [Test]
    public async Task SubscriptionChargeback_RecordsUserHistory()
    {
        // Arrange
        var userId = 103;
        var paypalSubId = "I-SUB-HISTORY-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "historyuser@example.com",
                NormalizedUserName = "HISTORYUSER@EXAMPLE.COM",
                Email = "historyuser@example.com",
                NormalizedEmail = "HISTORYUSER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 4,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 4,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-SUB-HIST",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-SUB-HIST/refund",
            System.Net.HttpStatusCode.OK, "{}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent, "");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-HIST-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SUB-HIST"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - verify user history was recorded
        _mockAdminNotificationService.Verify(
            x => x.RecordUserHistoryAsync(
                userId,
                "historyuser@example.com",
                Common.Helpers.UserHistoryEventTypes.ChargebackReceived,
                It.Is<string>(s => s.Contains("Chargeback") && s.Contains(paypalSubId)),
                null, null),
            Times.Once);
    }

    [Test]
    public async Task SubscriptionChargeback_SendsAdminNotification()
    {
        // Arrange
        var userId = 104;
        var paypalSubId = "I-SUB-ADMIN-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "adminnotify@example.com",
                NormalizedUserName = "ADMINNOTIFY@EXAMPLE.COM",
                Email = "adminnotify@example.com",
                NormalizedEmail = "ADMINNOTIFY@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 5,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 5,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-ADMIN-001",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-ADMIN-001/refund",
            System.Net.HttpStatusCode.OK, "{}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent, "");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-ADMIN-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-ADMIN-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - verify admin notification email was sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Chargeback")),
                It.Is<string>(b => b.Contains("PROCESSED_SUBSCRIPTION"))),
            Times.Once);
    }

    #endregion

    #region Tip Chargeback - Creator Email on Paid Tip

    [Test]
    public async Task TipChargeback_PaidTip_SendsPayoutReversalEmailToCreator()
    {
        // Arrange
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 200,
                UserName = "tipper@example.com",
                NormalizedUserName = "TIPPER@EXAMPLE.COM",
                Email = "tipper@example.com",
                NormalizedEmail = "TIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Users.Add(new ApplicationUser
            {
                Id = 201,
                UserName = "artist@example.com",
                NormalizedUserName = "ARTIST@EXAMPLE.COM",
                Email = "artist@example.com",
                NormalizedEmail = "ARTIST@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 10,
                UserId = 201,
                DisplayName = "Test Artist"
            });

            ctx.SongMetadata.Add(new SongMetadata
            {
                Id = 50,
                SongTitle = "My Hit Song",
                Mp3BlobPath = "songs/hit.mp3"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 100,
                TipperUserId = 200,
                CreatorId = 10,
                SongMetadataId = 50,
                Amount = 15.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = "ORDER-PAID-001",
                PayPalCaptureId = "CAPTURE-PAID-001",
                CapturedAt = DateTime.UtcNow.AddDays(-14),
                PaidAt = DateTime.UtcNow.AddDays(-5),
                CreatedAt = DateTime.UtcNow.AddDays(-14)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-CREATOR-001"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""15.00"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-PAID-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // Verify email sent to creator/artist
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "artist@example.com",
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.Is<string>(b => b.Contains("Test Artist") && b.Contains("My Hit Song") && b.Contains("15.00"))),
            Times.Once);

        // Verify email also sent to admin
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.Is<string>(b => b.Contains("Test Artist") && b.Contains("My Hit Song"))),
            Times.Once);
    }

    [Test]
    public async Task TipChargeback_PaidTip_IncludesPayoutReversalWarningInLog()
    {
        // Arrange
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 210,
                UserName = "tipper2@example.com",
                NormalizedUserName = "TIPPER2@EXAMPLE.COM",
                Email = "tipper2@example.com",
                NormalizedEmail = "TIPPER2@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 11,
                UserId = 210,
                DisplayName = "Creator Two"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 101,
                TipperUserId = 210,
                CreatorId = 11,
                Amount = 25.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = "ORDER-PAID-002",
                PayPalCaptureId = "CAPTURE-PAID-002",
                CapturedAt = DateTime.UtcNow.AddDays(-21),
                PaidAt = DateTime.UtcNow.AddDays(-10),
                CreatedAt = DateTime.UtcNow.AddDays(-21)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-WARN-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-PAID-002"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - ChargebackLog notes should contain payout reversal warning
        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Notes, Does.Contain("already paid out"));
        Assert.That(log.Notes, Does.Contain("payout reversal"));
        Assert.That(log.Status, Is.EqualTo("PROCESSED_TIP"));
    }

    [Test]
    public async Task TipChargeback_ClearedTip_DoesNotSendPayoutReversalEmail()
    {
        // Arrange - tip that is Cleared but NOT yet Paid (no reversal needed)
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 220,
                UserName = "tipper3@example.com",
                NormalizedUserName = "TIPPER3@EXAMPLE.COM",
                Email = "tipper3@example.com",
                NormalizedEmail = "TIPPER3@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Users.Add(new ApplicationUser
            {
                Id = 221,
                UserName = "creator3@example.com",
                NormalizedUserName = "CREATOR3@EXAMPLE.COM",
                Email = "creator3@example.com",
                NormalizedEmail = "CREATOR3@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 12,
                UserId = 221,
                DisplayName = "Creator Three"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 102,
                TipperUserId = 220,
                CreatorId = 12,
                Amount = 5.00m,
                Status = TipStatus.Cleared,
                PayPalOrderId = "ORDER-CLEARED-001",
                PayPalCaptureId = "CAPTURE-CLEARED-001",
                CapturedAt = DateTime.UtcNow.AddDays(-10),
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-CLEARED-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-CLEARED-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - payout reversal email should NOT be sent (tip wasn't paid out)
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "creator3@example.com",
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.IsAny<string>()),
            Times.Never);

        // Verify tip status changed to Chargeback
        using var context = new AppDbContext(_dbOptions);
        var tip = await context.Tips.FirstAsync(t => t.Id == 102);
        Assert.That(tip.Status, Is.EqualTo(TipStatus.Chargeback));
    }

    [Test]
    public async Task TipChargeback_PendingTip_DoesNotSendPayoutReversalEmail()
    {
        // Arrange - tip still in pending state
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 230,
                UserName = "tipper4@example.com",
                NormalizedUserName = "TIPPER4@EXAMPLE.COM",
                Email = "tipper4@example.com",
                NormalizedEmail = "TIPPER4@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 13,
                UserId = 230,
                DisplayName = "Creator Four"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 103,
                TipperUserId = 230,
                CreatorId = 13,
                Amount = 3.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = "ORDER-PENDING-001",
                PayPalCaptureId = "CAPTURE-PENDING-001",
                CapturedAt = DateTime.UtcNow.AddDays(-2),
                CreatedAt = DateTime.UtcNow.AddDays(-2)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-PENDING-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-PENDING-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - no payout reversal email
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.IsAny<string>()),
            Times.Never);

        // Verify tip still changed to Chargeback
        using var context = new AppDbContext(_dbOptions);
        var tip = await context.Tips.FirstAsync(t => t.Id == 103);
        Assert.That(tip.Status, Is.EqualTo(TipStatus.Chargeback));
    }

    [Test]
    public async Task TipChargeback_IncludesCreatorAndSongDetailsInEmail()
    {
        // Arrange
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 240,
                UserName = "tipper5@example.com",
                NormalizedUserName = "TIPPER5@EXAMPLE.COM",
                Email = "tipper5@example.com",
                NormalizedEmail = "TIPPER5@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Users.Add(new ApplicationUser
            {
                Id = 241,
                UserName = "detailedartist@example.com",
                NormalizedUserName = "DETAILEDARTIST@EXAMPLE.COM",
                Email = "detailedartist@example.com",
                NormalizedEmail = "DETAILEDARTIST@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 14,
                UserId = 241,
                DisplayName = "Detailed Artist"
            });

            ctx.SongMetadata.Add(new SongMetadata
            {
                Id = 51,
                SongTitle = "Detailed Hit Song",
                Mp3BlobPath = "songs/detailed-hit.mp3"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 104,
                TipperUserId = 240,
                CreatorId = 14,
                SongMetadataId = 51,
                Amount = 20.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = "ORDER-DETAIL-001",
                PayPalCaptureId = "CAPTURE-DETAIL-001",
                CapturedAt = DateTime.UtcNow.AddDays(-20),
                PaidAt = DateTime.UtcNow.AddDays(-7),
                CreatedAt = DateTime.UtcNow.AddDays(-20)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-DETAIL-001"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""dispute_life_cycle_stage"": ""INQUIRY"",
                ""dispute_channel"": ""INTERNAL"",
                ""dispute_amount"": { ""value"": ""20.00"", ""currency_code"": ""USD"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-DETAIL-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - verify email contains all relevant details
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "detailedartist@example.com",
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.Is<string>(b =>
                    b.Contains("Detailed Artist") &&
                    b.Contains("Detailed Hit Song") &&
                    b.Contains("20.00") &&
                    b.Contains("PP-D-DETAIL-001") &&
                    b.Contains("CAPTURE-DETAIL-001") &&
                    b.Contains("MERCHANDISE_OR_SERVICE_NOT_RECEIVED"))),
            Times.Once);
    }

    #endregion

    #region Tip Chargeback - Admin Notification

    [Test]
    public async Task TipChargeback_ClearedTip_SendsAdminNotification()
    {
        // Arrange
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 250,
                UserName = "admintipper@example.com",
                NormalizedUserName = "ADMINTIPPER@EXAMPLE.COM",
                Email = "admintipper@example.com",
                NormalizedEmail = "ADMINTIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 15,
                UserId = 250,
                DisplayName = "Admin Test Creator"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 105,
                TipperUserId = 250,
                CreatorId = 15,
                Amount = 7.50m,
                Status = TipStatus.Cleared,
                PayPalOrderId = "ORDER-ADMINTIP-001",
                PayPalCaptureId = "CAPTURE-ADMINTIP-001",
                CapturedAt = DateTime.UtcNow.AddDays(-10),
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-ADMINTIP-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-ADMINTIP-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - admin chargeback notification
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Chargeback")),
                It.Is<string>(b => b.Contains("PROCESSED_TIP"))),
            Times.Once);
    }

    #endregion

    #region No Transaction Found

    [Test]
    public async Task HandleWebhook_NoDisputedTransactions_CreatesLogAndNotifiesAdmin()
    {
        // Arrange
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-NOTX-001"",
                ""reason"": ""UNAUTHORISED"",
                ""dispute_life_cycle_stage"": ""CHARGEBACK"",
                ""dispute_channel"": ""EXTERNAL"",
                ""dispute_amount"": { ""value"": ""9.99"", ""currency_code"": ""USD"" }
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
        Assert.That(log.Status, Is.EqualTo("NO_TRANSACTION_FOUND"));
        Assert.That(log.Notes, Does.Contain("No seller_transaction_id"));

        // Admin notification sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Chargeback")),
                It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task HandleWebhook_NoMatchingTransaction_CreatesLogWithDetails()
    {
        // Arrange - transaction that matches neither subscription nor tip
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-NOMATCH-001"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""dispute_life_cycle_stage"": ""INQUIRY"",
                ""dispute_channel"": ""INTERNAL"",
                ""dispute_amount"": { ""value"": ""49.99"", ""currency_code"": ""GBP"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""PHANTOM-TXN-999"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Status, Is.EqualTo("NO_TRANSACTION_FOUND"));
        Assert.That(log.SellerTransactionId, Is.EqualTo("PHANTOM-TXN-999"));
        Assert.That(log.PayPalDisputeId, Is.EqualTo("PP-D-NOMATCH-001"));
        Assert.That(log.Amount, Is.EqualTo("49.99 GBP"));
    }

    #endregion

    #region Webhook Signature Verification

    [Test]
    public async Task HandleWebhook_WithRealWebhookId_RequiresSignatureHeaders()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns("WH-REAL-ID-12345");

        var body = @"{""event_type"":""CUSTOMER.DISPUTE.CREATED"",""resource"":{}}";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.ContentLength = body.Length;
        // Missing signature headers
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task HandleWebhook_WithNullWebhookId_SkipsVerification()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["PayPal:WebhookId"]).Returns((string)null);

        var body = @"{""event_type"":""PAYMENT.SALE.COMPLETED"",""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task HandleWebhook_DisputeWithMissingResource_StillReturnsOk()
    {
        // Arrange - event with no resource property
        var body = @"{""event_type"": ""CUSTOMER.DISPUTE.CREATED""}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert - should return OK (don't block webhooks)
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_DisputeWithMissingDisputeId_UsesUnknownDefault()
    {
        // Arrange
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""reason"": ""UNAUTHORISED""
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.PayPalDisputeId, Is.EqualTo("UNKNOWN"));
    }

    [Test]
    public async Task HandleWebhook_DisputeAmountWithoutCurrency_StoresValueOnly()
    {
        // Arrange
        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-NOCURR-001"",
                ""dispute_amount"": { ""value"": ""7.99"" },
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-NOCURR"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Amount, Is.EqualTo("7.99"));
    }

    [Test]
    public async Task HandleWebhook_TipChargeback_StatusChangedFromRefunded()
    {
        // Arrange - tip that was already refunded
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 260,
                UserName = "refundtipper@example.com",
                NormalizedUserName = "REFUNDTIPPER@EXAMPLE.COM",
                Email = "refundtipper@example.com",
                NormalizedEmail = "REFUNDTIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 16,
                UserId = 260,
                DisplayName = "Refund Creator"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 106,
                TipperUserId = 260,
                CreatorId = 16,
                Amount = 2.00m,
                Status = TipStatus.Refunded,
                PayPalOrderId = "ORDER-REFUND-001",
                PayPalCaptureId = "CAPTURE-REFUND-001",
                CapturedAt = DateTime.UtcNow.AddDays(-5),
                CreatedAt = DateTime.UtcNow.AddDays(-5)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-REFUND-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-REFUND-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - tip status should change to Chargeback even from Refunded
        using var context = new AppDbContext(_dbOptions);
        var tip = await context.Tips.FirstAsync(t => t.Id == 106);
        Assert.That(tip.Status, Is.EqualTo(TipStatus.Chargeback));

        var log = await context.ChargebackLogs.FirstOrDefaultAsync();
        Assert.That(log, Is.Not.Null);
        Assert.That(log.Notes, Does.Contain("Refunded").And.Contain("Chargeback"));
    }

    [Test]
    public async Task HandleWebhook_SuppressesAllNotifications_WhenDisabled()
    {
        // Arrange - disable ALL notifications
        _mockAdminNotificationService.Setup(x =>
            x.IsNotificationEnabledAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-SUPPRESS-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-SUPPRESS"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - admin chargeback email should NOT be sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Chargeback Received")),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task HandleWebhook_WhitespaceBody_ReturnsBadRequest()
    {
        // Arrange
        SetRequestBody("   \t\n   ");

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleWebhook_MultipleDisputesForSameTransaction_CreatesMultipleLogs()
    {
        // Arrange - seed tip data
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 270,
                UserName = "multitipper@example.com",
                NormalizedUserName = "MULTITIPPER@EXAMPLE.COM",
                Email = "multitipper@example.com",
                NormalizedEmail = "MULTITIPPER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Creators.Add(new Creator
            {
                Id = 17,
                UserId = 270,
                DisplayName = "Multi Creator"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 107,
                TipperUserId = 270,
                CreatorId = 17,
                Amount = 10.00m,
                Status = TipStatus.Cleared,
                PayPalOrderId = "ORDER-MULTI-001",
                PayPalCaptureId = "CAPTURE-MULTI-001",
                CapturedAt = DateTime.UtcNow.AddDays(-8),
                CreatedAt = DateTime.UtcNow.AddDays(-8)
            });

            await ctx.SaveChangesAsync();
        }

        // Process first dispute
        var body1 = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-MULTI-A"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-MULTI-001"" }
                ]
            }
        }";
        SetRequestBody(body1);
        await _controller.HandleWebhook();

        // The tip is now in Chargeback status. Process second dispute for same transaction
        var body2 = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-MULTI-B"",
                ""reason"": ""MERCHANDISE_OR_SERVICE_NOT_RECEIVED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-MULTI-001"" }
                ]
            }
        }";
        SetRequestBody(body2);
        await _controller.HandleWebhook();

        // Assert - both disputes should create separate log entries
        using var context = new AppDbContext(_dbOptions);
        var logs = await context.ChargebackLogs.ToListAsync();
        Assert.That(logs, Has.Count.EqualTo(2));
        Assert.That(logs.Select(l => l.PayPalDisputeId), Does.Contain("PP-D-MULTI-A"));
        Assert.That(logs.Select(l => l.PayPalDisputeId), Does.Contain("PP-D-MULTI-B"));
    }

    #endregion

    #region Subscription Chargeback - Refund Handling

    [Test]
    public async Task SubscriptionChargeback_AlreadyRefunded_LogsIdempotentRefund()
    {
        // Arrange
        var userId = 300;
        var paypalSubId = "I-SUB-REFUNDED-001";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "refundeduser@example.com",
                NormalizedUserName = "REFUNDEDUSER@EXAMPLE.COM",
                Email = "refundeduser@example.com",
                NormalizedEmail = "REFUNDEDUSER@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 10,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 10,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        // Mock refund returning 422 CAPTURE_FULLY_REFUNDED (idempotent)
        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-REFUNDED-001",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-REFUNDED-001/refund",
            System.Net.HttpStatusCode.UnprocessableEntity,
            "{\"details\": [{\"issue\": \"CAPTURE_FULLY_REFUNDED\"}]}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NoContent, "");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-REFUNDED-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-REFUNDED-001"" }
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
        Assert.That(log.Status, Is.EqualTo("PROCESSED_SUBSCRIPTION"));
        Assert.That(log.Notes, Does.Contain("already fully refunded"));
    }

    [Test]
    public async Task SubscriptionChargeback_PayPalSubscriptionAlreadyCancelled_StillProcesses()
    {
        // Arrange
        var userId = 310;
        var paypalSubId = "I-SUB-ALREADY-CANCELLED";

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "alreadycancelled@example.com",
                NormalizedUserName = "ALREADYCANCELLED@EXAMPLE.COM",
                Email = "alreadycancelled@example.com",
                NormalizedEmail = "ALREADYCANCELLED@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Subscriptions.Add(new Subscription
            {
                Id = 11,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE",
                MonthlyPrice = 3.99m
            });

            await ctx.SaveChangesAsync();
        }

        _mockSubscriptionService.Setup(x => x.GetSubscriptionByPayPalIdAsync(paypalSubId))
            .ReturnsAsync(new Subscription
            {
                Id = 11,
                UserId = userId,
                PayPalSubscriptionId = paypalSubId,
                Status = "ACTIVE"
            });

        // Cancel returns 404 (already cancelled)
        var mockHandler = CreateMockHttpHandler(
            "v1/payments/sale/TXN-ALREADY-001",
            System.Net.HttpStatusCode.OK,
            $"{{\"billing_agreement_id\": \"{paypalSubId}\"}}");
        AddMockHttpResponse(mockHandler, "v2/payments/captures/TXN-ALREADY-001/refund",
            System.Net.HttpStatusCode.OK, "{}");
        AddMockHttpResponse(mockHandler, $"v1/billing/subscriptions/{paypalSubId}/cancel",
            System.Net.HttpStatusCode.NotFound, "{\"name\":\"RESOURCE_NOT_FOUND\"}");
        AddMockHttpResponse(mockHandler, "v1/oauth2/token",
            System.Net.HttpStatusCode.OK, "{\"access_token\": \"test-token\"}");

        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(mockHandler.Object));

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-ALREADY-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""TXN-ALREADY-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());

        // User should still be blocked
        using var context = new AppDbContext(_dbOptions);
        var user = await context.Users.FirstAsync(u => u.Id == userId);
        Assert.That(user.IsSubscriptionBlocked, Is.True);
    }

    #endregion

    #region Tip Chargeback - No Creator Email

    [Test]
    public async Task TipChargeback_PaidTip_CreatorWithNoEmail_StillSendsAdminEmail()
    {
        // Arrange - creator user has no email
        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Users.Add(new ApplicationUser
            {
                Id = 280,
                UserName = "tipper6@example.com",
                NormalizedUserName = "TIPPER6@EXAMPLE.COM",
                Email = "tipper6@example.com",
                NormalizedEmail = "TIPPER6@EXAMPLE.COM",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            ctx.Users.Add(new ApplicationUser
            {
                Id = 281,
                UserName = "noemail",
                NormalizedUserName = "NOEMAIL",
                EmailConfirmed = false,
                SecurityStamp = Guid.NewGuid().ToString()
                // No Email set
            });

            ctx.Creators.Add(new Creator
            {
                Id = 18,
                UserId = 281,
                DisplayName = "No Email Creator"
            });

            ctx.Tips.Add(new Tip
            {
                Id = 108,
                TipperUserId = 280,
                CreatorId = 18,
                Amount = 8.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = "ORDER-NOEMAIL-001",
                PayPalCaptureId = "CAPTURE-NOEMAIL-001",
                CapturedAt = DateTime.UtcNow.AddDays(-15),
                PaidAt = DateTime.UtcNow.AddDays(-5),
                CreatedAt = DateTime.UtcNow.AddDays(-15)
            });

            await ctx.SaveChangesAsync();
        }

        var body = @"{
            ""event_type"": ""CUSTOMER.DISPUTE.CREATED"",
            ""resource"": {
                ""dispute_id"": ""PP-D-NOEMAIL-001"",
                ""reason"": ""UNAUTHORISED"",
                ""disputed_transactions"": [
                    { ""seller_transaction_id"": ""CAPTURE-NOEMAIL-001"" }
                ]
            }
        }";
        SetRequestBody(body);

        // Act
        await _controller.HandleWebhook();

        // Assert - admin should still get the payout reversal email
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Payout Reversal")),
                It.IsAny<string>()),
            Times.Once);
    }

    #endregion

    #region Unrecognized Events

    [Test]
    public async Task HandleWebhook_PaymentSaleCompleted_ReturnsOk()
    {
        // Arrange
        var body = @"{""event_type"":""PAYMENT.SALE.COMPLETED"",""resource"":{""id"":""123""}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_BillingSubscriptionActivated_ReturnsOk()
    {
        // Arrange
        var body = @"{""event_type"":""BILLING.SUBSCRIPTION.ACTIVATED"",""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task HandleWebhook_NoEventType_ReturnsOk()
    {
        // Arrange
        var body = @"{""resource"":{}}";
        SetRequestBody(body);

        // Act
        var result = await _controller.HandleWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    #endregion

    #region Helper Methods

    private Mock<HttpMessageHandler> CreateMockHttpHandler(
        string expectedPath, System.Net.HttpStatusCode statusCode, string responseContent)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains(expectedPath)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });

        // Default handler for unmatched paths (token endpoint etc.)
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("v1/oauth2/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\": \"test-token\"}", Encoding.UTF8, "application/json")
            });

        return mockHandler;
    }

    private void AddMockHttpResponse(
        Mock<HttpMessageHandler> mockHandler,
        string path, System.Net.HttpStatusCode statusCode, string responseContent)
    {
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains(path)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });
    }

    #endregion
}
