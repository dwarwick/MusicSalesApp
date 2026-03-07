#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class StreamPayoutServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<StreamPayoutService>> _mockLogger;
    private Mock<ITaxBanditsService> _mockTaxBanditsService;
    private Mock<ITipService> _mockTipService;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private StreamPayoutService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"StreamPayoutTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<StreamPayoutService>>();
        _mockEmailService = new Mock<IEmailService>();
        _mockTaxBanditsService = new Mock<ITaxBanditsService>();
        _mockTipService = new Mock<ITipService>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();

        _service = new StreamPayoutService(
            _mockContextFactory.Object,
            _mockEmailService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockTaxBanditsService.Object,
            _mockTipService.Object,
            _mockAppSettingsService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task SeedCreatorWithPayouts(decimal tipAmount = 0m)
    {
        var creatorUser = new ApplicationUser
        {
            Id = 1, UserName = "creator@test.com", Email = "creator@test.com",
            NormalizedEmail = "CREATOR@TEST.COM", NormalizedUserName = "CREATOR@TEST.COM"
        };
        _context.Users.Add(creatorUser);

        var creator = new Creator
        {
            Id = 1, UserId = 1, IsActive = true,
            DisplayName = "Test Creator",
            PayPalEmail = "creator@test.com",
            TaxResidencyType = TaxResidencyType.US,
            TaxBanditsPayeeRef = "creator@test.com"
        };
        _context.Creators.Add(creator);

        var songMetadata = new SongMetadata
        {
            Id = 1, Mp3BlobPath = "test.mp3", SongTitle = "Test Song",
            CreatorId = 1
        };
        _context.SongMetadata.Add(songMetadata);

        await _context.SaveChangesAsync();

        // Add a stream payout record with tip amount
        _context.StreamPayouts.Add(new StreamPayout
        {
            CreatorId = 1,
            SongMetadataId = 1,
            NumberOfStreams = 2000,
            RatePerStream = 0.005m,
            GrossAmount = 10.00m,
            WithholdingRate = 0m,
            WithheldAmount = 0m,
            NetAmount = 10.00m,
            TipAmount = tipAmount,
            PayPalTransactionId = "PAYOUT-TXN-001",
            TaxBanditsStatus = "Pending",
            PaymentDate = DateTime.UtcNow.AddDays(-1)
        });
        await _context.SaveChangesAsync();
    }

    // ==================== RetryPending1099TransactionsAsync Tests ====================

    [Test]
    public async Task RetryPending1099_IncludesTipAmountInGrossAmount()
    {
        // Arrange - payout with $10 streams + $5 tips
        await SeedCreatorWithPayouts(tipAmount: 5.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        Form1099Transaction? capturedTransaction = null;
        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .Callback<List<Form1099Transaction>, CancellationToken>((txns, _) => capturedTransaction = txns.FirstOrDefault())
            .ReturnsAsync(new Form1099TransactionResponse { Success = true, TransactionId = "TXN-123" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - GrossAmount should include tip amount (10 + 5 = 15)
        Assert.That(capturedTransaction, Is.Not.Null);
        Assert.That(capturedTransaction!.GrossAmount, Is.EqualTo(15.00m)); // GrossAmount + TipAmount
        Assert.That(updated, Is.GreaterThan(0));
    }

    [Test]
    public async Task RetryPending1099_WithoutTips_UsesOnlyGrossAmount()
    {
        // Arrange - payout with only streams, no tips
        await SeedCreatorWithPayouts(tipAmount: 0m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        Form1099Transaction? capturedTransaction = null;
        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .Callback<List<Form1099Transaction>, CancellationToken>((txns, _) => capturedTransaction = txns.FirstOrDefault())
            .ReturnsAsync(new Form1099TransactionResponse { Success = true, TransactionId = "TXN-456" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - GrossAmount should be just the stream amount
        Assert.That(capturedTransaction, Is.Not.Null);
        Assert.That(capturedTransaction!.GrossAmount, Is.EqualTo(10.00m));
        Assert.That(updated, Is.GreaterThan(0));
    }

    [Test]
    public async Task RetryPending1099_SkipsDuringMaintenance()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 5.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(true);

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert
        Assert.That(updated, Is.EqualTo(0));
        _mockTaxBanditsService.Verify(
            x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default),
            Times.Never);
    }

    [Test]
    public async Task RetryPending1099_NoPendingPayouts_ReturnsZero()
    {
        // Arrange - no payouts in database
        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert
        Assert.That(updated, Is.EqualTo(0));
        _mockTaxBanditsService.Verify(
            x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default),
            Times.Never);
    }

    [Test]
    public async Task RetryPending1099_FailedSubmission_KeepsPendingStatus()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 3.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .ReturnsAsync(new Form1099TransactionResponse { Success = false, ErrorMessage = "Service unavailable" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - no payouts should be marked as updated
        Assert.That(updated, Is.EqualTo(0));

        // Verify payout still has Pending status
        using var verifyContext = new AppDbContext(_contextOptions);
        var payout = await verifyContext.StreamPayouts.FirstAsync();
        Assert.That(payout.TaxBanditsStatus, Is.EqualTo("Pending"));
    }

    // ==================== GetAllPayoutsAsync Tests ====================

    [Test]
    public async Task GetAllPayoutsAsync_ReturnsAllPayoutsWithNavigationProperties()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 7.50m);

        // Act
        var payouts = await _service.GetAllPayoutsAsync();

        // Assert
        Assert.That(payouts, Has.Count.EqualTo(1));
        Assert.That(payouts[0].TipAmount, Is.EqualTo(7.50m));
        Assert.That(payouts[0].GrossAmount, Is.EqualTo(10.00m));
        Assert.That(payouts[0].Creator, Is.Not.Null);
        Assert.That(payouts[0].SongMetadata, Is.Not.Null);
    }

    // ==================== StreamPayout TipAmount Model Tests ====================

    [Test]
    public void StreamPayout_TipAmount_DefaultsToZero()
    {
        // Act
        var payout = new StreamPayout();

        // Assert
        Assert.That(payout.TipAmount, Is.EqualTo(0m));
    }

    [Test]
    public void StreamPayout_TipAmount_CanBeSet()
    {
        // Act
        var payout = new StreamPayout { TipAmount = 25.50m };

        // Assert
        Assert.That(payout.TipAmount, Is.EqualTo(25.50m));
    }
}
