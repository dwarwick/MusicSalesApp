using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class CreatorServiceTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ILogger<CreatorService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private Mock<ICreatorPersonaService> _mockCreatorPersonaService;
    private Mock<ICreatorEmailService> _mockCreatorEmailService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private AppDbContext _context;
    private CreatorService _service;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockLogger = new Mock<ILogger<CreatorService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();
        _mockAppSettingsService.Setup(x => x.GetStreamPayRateAsync()).ReturnsAsync(0.005m);
        _mockAppSettingsService.Setup(x => x.GetStreamQualifyingSecondsAsync()).ReturnsAsync(30);
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockAdminNotificationService
            .Setup(x => x.RecordUserHistoryAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockCreatorPersonaService = new Mock<ICreatorPersonaService>();
        _mockCreatorPersonaService.Setup(x => x.DeleteAllPersonasForCreatorAsync(It.IsAny<int>())).ReturnsAsync(0);
        _mockCreatorEmailService = new Mock<ICreatorEmailService>();
        _mockCreatorEmailService
            .Setup(x => x.SendCreatorWelcomeEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(true);

        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _mockUserManager.Setup(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), Roles.Creator))
            .ReturnsAsync(false);
        _mockUserManager.Setup(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), Roles.Creator))
            .ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.RemoveFromRoleAsync(It.IsAny<ApplicationUser>(), Roles.Creator))
            .ReturnsAsync(IdentityResult.Success);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(options);
        _context = new AppDbContext(options);

        _service = new CreatorService(
            _contextFactory,
            _mockStorageService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockAppSettingsService.Object,
            _mockAdminNotificationService.Object,
            _mockCreatorPersonaService.Object,
            _mockCreatorEmailService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    #region GetActiveCreatorIdAsync

    [Test]
    public async Task GetActiveCreatorIdAsync_ReturnsId_ForActiveCreator()
    {
        _context.Creators.Add(new Creator { UserId = 42, DisplayName = "Active", IsActive = true });
        await _context.SaveChangesAsync();

        var creatorId = await _service.GetActiveCreatorIdAsync(42);

        Assert.That(creatorId, Is.Not.Null);
    }

    [Test]
    public async Task GetActiveCreatorIdAsync_ReturnsNull_ForDeactivatedCreator()
    {
        // This is the filter that stops a deactivated creator from keeping mobile creator
        // privileges - api/subscription/status reports IsCreator straight from this result.
        _context.Creators.Add(new Creator { UserId = 43, DisplayName = "Deactivated", IsActive = false });
        await _context.SaveChangesAsync();

        var creatorId = await _service.GetActiveCreatorIdAsync(43);

        Assert.That(creatorId, Is.Null);
    }

    [Test]
    public async Task GetActiveCreatorIdAsync_ReturnsNull_WhenUserHasNoCreatorRecord()
    {
        var creatorId = await _service.GetActiveCreatorIdAsync(999);

        Assert.That(creatorId, Is.Null);
    }

    #endregion

    #region DeleteCreatorSongAsync storage cleanup

    [Test]
    public async Task DeleteCreatorSongAsync_GuidSong_DeletesEveryBlobIncludingTheSharingImage()
    {
        var mediaGuid = Guid.NewGuid();
        var creator = new Creator { Id = 0, DisplayName = "Test Creator" };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            MediaGuid = mediaGuid,
            SongTitle = "Night Drive",
            CreatorId = creator.Id,
            Mp3BlobPath = SongMediaPaths.Playback(mediaGuid),
            OriginalAudioBlobPath = SongMediaPaths.OriginalAudio(mediaGuid, ".wav"),
            ImageBlobPath = SongMediaPaths.CoverArt(mediaGuid, ".png"),
            OriginalCoverArtBlobPath = SongMediaPaths.OriginalCoverArt(mediaGuid, ".png")
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        var deleted = await _service.DeleteCreatorSongAsync(song.Id, creator.Id);

        Assert.That(deleted, Is.True);
        _mockStorageService.Verify(s => s.DeleteAsync(SongMediaPaths.Playback(mediaGuid)), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync(SongMediaPaths.OriginalAudio(mediaGuid, ".wav")), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync(SongMediaPaths.CoverArt(mediaGuid, ".png")), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync(SongMediaPaths.OriginalCoverArt(mediaGuid, ".png")), Times.Once);
        // Previously leaked on every delete because the path is derived, not stored.
        _mockStorageService.Verify(s => s.DeleteAsync(SongMediaPaths.FacebookImage(mediaGuid)), Times.Once);
    }

    [Test]
    public async Task DeleteCreatorSongAsync_LegacySong_DeletesItsUnderscoreSuffixedSharingImage()
    {
        var creator = new Creator { Id = 0, DisplayName = "Test Creator" };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            SongTitle = "Night Drive",
            CreatorId = creator.Id,
            Mp3BlobPath = "Night Drive/Night Drive.mp3",
            ImageBlobPath = "Night Drive/Night Drive.jpg"
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        await _service.DeleteCreatorSongAsync(song.Id, creator.Id);

        _mockStorageService.Verify(s => s.DeleteAsync("Night Drive/Night Drive.mp3"), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync("Night Drive/Night Drive.jpg"), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync("Night Drive/Night Drive_fb.jpg"), Times.Once);
        // The superseded PNG sharing image and the pre-resized renditions are derived rather than
        // stored, so deleting a song has to name them explicitly or they leak.
        _mockStorageService.Verify(s => s.DeleteAsync("Night Drive/Night Drive_fb.png"), Times.Once);
        _mockStorageService.Verify(s => s.DeleteAsync("Night Drive/Night Drive.jpg.w320.webp"), Times.Once);
    }

    #endregion

    #region ResetCreatorOnboardingAsync Tests

    [Test]
    public async Task ResetCreatorOnboardingAsync_SetsOnboardingStatusToCompleted()
    {
        // Arrange — creator who stopped selling (Suspended)
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false,
            PaymentsReceivable = false,
            PrimaryEmailConfirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted));
        Assert.That(result.PayPalEmail, Is.EqualTo("new@paypal.com"));
        Assert.That(result.PayPalAccountAffirmed, Is.True);
        Assert.That(result.PaymentsReceivable, Is.True);
        Assert.That(result.PrimaryEmailConfirmed, Is.True);
        Assert.That(result.OnboardedAt, Is.Not.Null);

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed),
            "OnboardingStatus should be Completed in the database after reset");
        Assert.That(saved.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted),
            "TaxFormStatus should be reset to NotStarted after re-signup");
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_PreservesExistingPayPal_WhenNoNewPayPalProvided()
    {
        var user = new ApplicationUser { UserName = "preserve@test.com", Email = "preserve@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            IsActive = false,
            PayPalEmail = "existing@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, null, false);

        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(result.PayPalEmail, Is.EqualTo("existing@paypal.com"));
        Assert.That(result.PayPalAccountAffirmed, Is.True);
        Assert.That(result.PaymentsReceivable, Is.True);
        Assert.That(result.PrimaryEmailConfirmed, Is.True);
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_ResetsTaxFormStatusFromCompleted()
    {
        // Arrange — returning creator who previously completed tax form
        var user = new ApplicationUser { UserName = "returning@test.com", Email = "returning@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert — TaxFormStatus must be reset so creator goes through TaxBandits email flow again
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted),
            "TaxFormStatus must be reset to NotStarted so the TaxBandits email is sent again");
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted));
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_FromConsentRevoked_SetsCompleted()
    {
        // Arrange — creator whose consent was revoked
        var user = new ApplicationUser { UserName = "revoked@test.com", Email = "revoked@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.ConsentRevoked,
            IsActive = false,
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_ThrowsForInvalidCreatorId()
    {
        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResetCreatorOnboardingAsync(9999, "test@paypal.com", true));
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_PreservesTaxFormStatus_WhenTaxFormCompletedAtIsSet()
    {
        // Arrange — returning creator who previously completed a tax form (TaxFormCompletedAt is set)
        var user = new ApplicationUser { UserName = "returning2@test.com", Email = "returning2@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            TaxFormStatus = TaxFormStatus.Completed,
            TaxFormCompletedAt = DateTime.UtcNow.AddMonths(-6),
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert — TaxFormStatus should be preserved since TaxFormCompletedAt is set
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed),
            "TaxFormStatus should be preserved for returning creators who already completed a tax form");
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed));
    }

    #endregion

    #region ActivateCreatorAsync Tests

    [Test]
    public async Task ActivateCreatorAsync_SetsIsActiveAndOnboardingCompleted()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "activate@test.com", Email = "activate@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ActivateCreatorAsync(creator.Id);

        // Assert
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
    }

    #endregion

    #region DeactivateCreatorAsync Tests

    [Test]
    public async Task DeactivateCreatorAsync_SetsIsActiveFalse_WithoutDeletingSongsOrStorage()
    {
        // Arrange — active creator with a published song
        var user = new ApplicationUser { UserName = "deactivate@test.com", Email = "deactivate@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            IsActive = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            CreatorId = creator.Id,
            SongTitle = "Still Standing",
            Mp3BlobPath = "Still Standing/Still Standing.mp3",
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act — admin suspends the creator (must remain reversible, unlike leaving/consent-revocation)
        var result = await _service.DeactivateCreatorAsync(creator.Id);

        // Assert
        Assert.That(result.IsActive, Is.False);
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Suspended));

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedSong = await verifyContext.SongMetadata.FindAsync(song.Id);
        Assert.That(savedSong!.IsActive, Is.True, "Deactivating a creator must not deactivate their songs.");

        _mockStorageService.Verify(
            s => s.DeleteAsync(It.IsAny<string>()),
            Times.Never,
            "Deactivating a creator (a reversible suspension) must not delete their media from storage.");
    }

    #endregion

    #region UpdateCreatorPayoutEmailAsync Tests

    [Test]
    public async Task UpdateCreatorPayoutEmailAsync_UpdatesPayPalEmail()
    {
        var user = new ApplicationUser { UserName = "payout@test.com", Email = "payout@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            PayPalEmail = "old@paypal.com",
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var result = await _service.UpdateCreatorPayoutEmailAsync(user.Id, "new@paypal.com", true);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PayPalEmail, Is.EqualTo("new@paypal.com"));
        Assert.That(result.PayPalAccountAffirmed, Is.True);
        Assert.That(result.PaymentsReceivable, Is.True);
        Assert.That(result.PrimaryEmailConfirmed, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(savedCreator.PayPalEmail, Is.EqualTo("new@paypal.com"));
        Assert.That(savedCreator.PayPalAccountAffirmed, Is.True);
        Assert.That(savedCreator.UpdatedAt, Is.GreaterThan(DateTime.MinValue));
    }

    [Test]
    public async Task ActivateCreatorAsync_StampsOnboardedAtAndRearmsTheNotice()
    {
        // OnboardedAt is named for this moment and used to be written only by the RE-onboarding
        // path, so it was null for most active creators. Re-arming matters too: someone who stops
        // being a creator and comes back is activating again, and should be told so again.
        var user = new ApplicationUser { UserName = "rearm@test.com", Email = "rearm@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, ActivationAnnouncedAt = DateTime.UtcNow.AddDays(-30) };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        await _service.ActivateCreatorAsync(creator.Id);

        await using var verify = await _contextFactory.CreateDbContextAsync();
        var saved = await verify.Creators.SingleAsync(c => c.Id == creator.Id);

        Assert.Multiple(() =>
        {
            Assert.That(saved.IsActive, Is.True);
            Assert.That(saved.OnboardedAt, Is.Not.Null, "the activation itself is what OnboardedAt records");
            Assert.That(saved.ActivationAnnouncedAt, Is.Null, "a fresh activation owes a fresh notice");
        });
    }

    [Test]
    public async Task StopBeingCreatorAsync_RearmsTheDeactivationNotice()
    {
        var user = new ApplicationUser { UserName = "stop@test.com", Email = "stop@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true,
            DeactivationAnnouncedAt = DateTime.UtcNow.AddDays(-30),
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        await _service.StopBeingCreatorAsync(user.Id);

        await using var verify = await _contextFactory.CreateDbContextAsync();
        var saved = await verify.Creators.SingleAsync(c => c.Id == creator.Id);

        Assert.Multiple(() =>
        {
            Assert.That(saved.IsActive, Is.False);
            Assert.That(saved.DeactivationAnnouncedAt, Is.Null);
        });
    }
    [Test]
    public async Task GetCreatorSongCountAsync_CountsOnlyThisCreatorsActiveSongs()
    {
        // The settings page shows this number in three places - the checklist, the catalogue
        // note, and the "this deletes N songs" warning on the way out - so counting a
        // deactivated song, or someone else's, is user-visible and wrong in the worst spot.
        var mine = new Creator { DisplayName = "Mine" };
        var theirs = new Creator { DisplayName = "Theirs" };
        _context.Creators.AddRange(mine, theirs);
        await _context.SaveChangesAsync();

        var a = Guid.NewGuid();
        _context.SongMetadata.Add(new SongMetadata
        {
            MediaGuid = a,
            SongTitle = "Kept One",
            CreatorId = mine.Id,
            Mp3BlobPath = SongMediaPaths.Playback(a),
            OriginalAudioBlobPath = SongMediaPaths.OriginalAudio(a, ".wav"),
            ImageBlobPath = SongMediaPaths.CoverArt(a, ".png"),
            OriginalCoverArtBlobPath = SongMediaPaths.OriginalCoverArt(a, ".png"),
        });
        var b = Guid.NewGuid();
        _context.SongMetadata.Add(new SongMetadata
        {
            MediaGuid = b,
            SongTitle = "Kept Two",
            CreatorId = mine.Id,
            Mp3BlobPath = SongMediaPaths.Playback(b),
            OriginalAudioBlobPath = SongMediaPaths.OriginalAudio(b, ".wav"),
            ImageBlobPath = SongMediaPaths.CoverArt(b, ".png"),
            OriginalCoverArtBlobPath = SongMediaPaths.OriginalCoverArt(b, ".png"),
        });
        var c = Guid.NewGuid();
        _context.SongMetadata.Add(new SongMetadata
        {
            MediaGuid = c,
            SongTitle = "Deactivated",
            CreatorId = mine.Id,
            Mp3BlobPath = SongMediaPaths.Playback(c),
            OriginalAudioBlobPath = SongMediaPaths.OriginalAudio(c, ".wav"),
            ImageBlobPath = SongMediaPaths.CoverArt(c, ".png"),
            OriginalCoverArtBlobPath = SongMediaPaths.OriginalCoverArt(c, ".png"),
            IsActive = false,
        });
        var d = Guid.NewGuid();
        _context.SongMetadata.Add(new SongMetadata
        {
            MediaGuid = d,
            SongTitle = "Someone Else",
            CreatorId = theirs.Id,
            Mp3BlobPath = SongMediaPaths.Playback(d),
            OriginalAudioBlobPath = SongMediaPaths.OriginalAudio(d, ".wav"),
            ImageBlobPath = SongMediaPaths.CoverArt(d, ".png"),
            OriginalCoverArtBlobPath = SongMediaPaths.OriginalCoverArt(d, ".png"),
        });
        await _context.SaveChangesAsync();

        var count = await _service.GetCreatorSongCountAsync(mine.Id);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetCreatorSongCountAsync_ReturnsZero_WhenNothingUploaded()
    {
        var creator = new Creator { DisplayName = "Empty" };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        Assert.That(await _service.GetCreatorSongCountAsync(creator.Id), Is.EqualTo(0));
    }
    [Test]
    public async Task UpdateCreatorPayoutEmailAsync_ReturnsNull_WhenCreatorDoesNotExist()
    {
        var result = await _service.UpdateCreatorPayoutEmailAsync(999, "new@paypal.com", true);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task UpdateCreatorPayoutEmailAsync_InvalidPayPalEmail_ThrowsAndDoesNotUpdate()
    {
        var user = new ApplicationUser { UserName = "badpayout@test.com", Email = "badpayout@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            PayPalEmail = "existing@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpdateCreatorPayoutEmailAsync(user.Id, "@angelaomalley72", true));

        Assert.That(ex!.Message, Is.EqualTo(PayoutEmailValidator.InvalidPayPalEmailMessage));

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(savedCreator.PayPalEmail, Is.EqualTo("existing@paypal.com"));
        Assert.That(savedCreator.PayPalAccountAffirmed, Is.True);
    }

    [Test]
    public async Task UpdateCreatorPayoutEmailAsync_EmptyEmailAndUnaffirmed_ClearsPayoutInfo()
    {
        var user = new ApplicationUser { UserName = "clearpayout@test.com", Email = "clearpayout@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalEmail = "existing@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var result = await _service.UpdateCreatorPayoutEmailAsync(user.Id, "   ", false);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PayPalEmail, Is.Null);
        Assert.That(result.PayPalAccountAffirmed, Is.False);
        Assert.That(result.PaymentsReceivable, Is.False);
        Assert.That(result.PrimaryEmailConfirmed, Is.False);
        Assert.That(result.IsFullyOnboarded, Is.False);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(savedCreator.PayPalEmail, Is.Null);
        Assert.That(savedCreator.PayPalAccountAffirmed, Is.False);
        Assert.That(savedCreator.PaymentsReceivable, Is.False);
        Assert.That(savedCreator.PrimaryEmailConfirmed, Is.False);
    }

    [Test]
    public async Task UpdateCreatorPayoutEmailAsync_EmptyEmailWithAffirmation_ThrowsAndDoesNotUpdate()
    {
        var user = new ApplicationUser { UserName = "emptyaffirmed@test.com", Email = "emptyaffirmed@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            PayPalEmail = "existing@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpdateCreatorPayoutEmailAsync(user.Id, " ", true));

        Assert.That(ex!.Message, Is.EqualTo(PayoutEmailValidator.PayPalEmailRequiredForAffirmationMessage));

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(savedCreator.PayPalEmail, Is.EqualTo("existing@paypal.com"));
        Assert.That(savedCreator.PayPalAccountAffirmed, Is.True);
        Assert.That(savedCreator.PaymentsReceivable, Is.True);
        Assert.That(savedCreator.PrimaryEmailConfirmed, Is.True);
    }

    #endregion

    #region StopBeingCreatorAsync → Re-signup Full Flow Test

    [Test]
    public async Task FullFlow_StopBeingCreator_ThenReSignup_CreatorCanBeActivated()
    {
        // Arrange — active creator
        var user = new ApplicationUser { UserName = "flow@test.com", Email = "flow@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            TaxFormCompletedAt = DateTime.UtcNow.AddMonths(-3),
            IsActive = true,
            PayPalEmail = "original@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Mock user manager for StopBeingCreatorAsync (it removes Creator role)
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator))
            .ReturnsAsync(true);
        _mockUserManager.Setup(x => x.RemoveFromRoleAsync(user, Roles.Creator))
            .ReturnsAsync(IdentityResult.Success);

        // Step 1: Stop being a creator
        await _service.StopBeingCreatorAsync(user.Id);

        // Verify suspended state
        await using (var ctx1 = await _contextFactory.CreateDbContextAsync())
        {
            var suspended = await ctx1.Creators.FindAsync(creator.Id);
            Assert.That(suspended!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Suspended));
            Assert.That(suspended.IsActive, Is.False);
            // TaxFormStatus should be preserved
            Assert.That(suspended.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed));
        }

        // Step 2: Re-signup (ResetCreatorOnboarding)
        await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Verify reset state — TaxFormStatus should be preserved since TaxFormCompletedAt is set
        await using (var ctx2 = await _contextFactory.CreateDbContextAsync())
        {
            var reset = await ctx2.Creators.FindAsync(creator.Id);
            Assert.That(reset!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed),
                "OnboardingStatus should be Completed after re-signup");
            Assert.That(reset.PayPalEmail, Is.EqualTo("new@paypal.com"));
            Assert.That(reset.PayPalAccountAffirmed, Is.True);
            Assert.That(reset.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed),
                "TaxFormStatus should be preserved for returning creators who already completed a tax form");
            // IsActive should still be false — needs ActivateCreatorAsync
            Assert.That(reset.IsActive, Is.False);
        }

        // Step 3: Activate (simulates webhook completing tax form check)
        await _service.ActivateCreatorAsync(creator.Id);

        // Verify final active state
        await using (var ctx3 = await _contextFactory.CreateDbContextAsync())
        {
            var active = await ctx3.Creators.FindAsync(creator.Id);
            Assert.That(active!.IsActive, Is.True);
            Assert.That(active.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        }
    }

    [Test]
    public async Task StopBeingCreatorAsync_ClearsCreatorAgreementAcceptance()
    {
        var user = new ApplicationUser { UserName = "stopagreement@test.com", Email = "stopagreement@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            CreatorAgreementAccepted = true,
            CreatorAgreementAcceptedAtUtc = DateTime.UtcNow.AddDays(-2),
            AcknowledgmentAccepted = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator))
            .ReturnsAsync(true);

        var result = await _service.StopBeingCreatorAsync(user.Id);

        Assert.That(result, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var stoppedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(stoppedCreator.IsActive, Is.False);
        Assert.That(stoppedCreator.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Suspended));
        Assert.That(stoppedCreator.CreatorAgreementAccepted, Is.False);
        Assert.That(stoppedCreator.CreatorAgreementAcceptedAtUtc, Is.Null);
        Assert.That(stoppedCreator.AcknowledgmentAccepted, Is.True, "Legacy acceptance is retained for historical compatibility.");
    }

    [Test]
    public async Task RevokeCreatorConsentAsync_ClearsCreatorAgreementAcceptance()
    {
        var user = new ApplicationUser { UserName = "revokeagreement@test.com", Email = "revokeagreement@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            IsActive = true,
            CreatorAgreementAccepted = true,
            CreatorAgreementAcceptedAtUtc = DateTime.UtcNow.AddDays(-2)
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var result = await _service.RevokeCreatorConsentAsync(creator.Id);

        Assert.That(result, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var revokedCreator = await verifyContext.Creators.SingleAsync(c => c.Id == creator.Id);
        Assert.That(revokedCreator.IsActive, Is.False);
        Assert.That(revokedCreator.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.ConsentRevoked));
        Assert.That(revokedCreator.CreatorAgreementAccepted, Is.False);
        Assert.That(revokedCreator.CreatorAgreementAcceptedAtUtc, Is.Null);
    }

    #endregion

    #region UpdateLocationCertificationAsync Tests

    [Test]
    public async Task UpdateLocationCertificationAsync_StoresAttestationData()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "attest@test.com", Email = "attest@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.NotStarted,
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.UpdateLocationCertificationAsync(
            creator.Id, CreatorLocationCertification.USPerson, true);

        // Assert
        Assert.That(result.LocationCertification, Is.EqualTo(CreatorLocationCertification.USPerson));
        Assert.That(result.AcknowledgmentAccepted, Is.True);
        Assert.That(result.AcknowledgmentDateTimeUtc, Is.Not.Null);

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.LocationCertification, Is.EqualTo(CreatorLocationCertification.USPerson));
        Assert.That(saved.AcknowledgmentAccepted, Is.True);
        Assert.That(saved.AcknowledgmentDateTimeUtc, Is.Not.Null);
    }

    [Test]
    public async Task UpdateLocationCertificationAsync_NonUSPersonInsideUS_StoresCorrectly()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "nonus@test.com", Email = "nonus@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.NotStarted,
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.UpdateLocationCertificationAsync(
            creator.Id, CreatorLocationCertification.NonUSPersonInsideUS, true);

        // Assert
        Assert.That(result.LocationCertification, Is.EqualTo(CreatorLocationCertification.NonUSPersonInsideUS));
        Assert.That(result.AcknowledgmentAccepted, Is.True);
    }

    [Test]
    public void UpdateLocationCertificationAsync_ThrowsForInvalidCreatorId()
    {
        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateLocationCertificationAsync(9999, CreatorLocationCertification.USPerson, true));
    }

    #endregion

    #region UpdatePayoutRequirementsAcknowledgmentAsync Tests

    [Test]
    public async Task UpdatePayoutRequirementsAcknowledgmentAsync_StoresAssertionAndRecordsHistory()
    {
        var user = new ApplicationUser { UserName = "assert@test.com", Email = "assert@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            PayoutRequirementsAcknowledged = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var result = await _service.UpdatePayoutRequirementsAcknowledgmentAsync(creator.Id, true);

        Assert.That(result.PayoutRequirementsAcknowledged, Is.True);
        Assert.That(result.PayoutRequirementsAcknowledgedAtUtc, Is.Not.Null);

        _mockAdminNotificationService.Verify(
            x => x.RecordUserHistoryAsync(
                user.Id,
                user.Email!,
                UserHistoryEventTypes.CreatorPayoutRequirementsAcknowledged,
                It.Is<string>(description => description.Contains("PayPal confirmation")),
                null,
                null),
            Times.Once);
    }

    #endregion

    #region StartOnboardingAsync Tests

    private CreatorOnboardingInput CreateValidOnboardingInput(int userId, string email = "test@test.com") => new()
    {
        UserId = userId,
        UserEmail = email,
        DisplayName = "Test Creator",
        Bio = "Test bio",
        PayPalEmail = "paypal@test.com",
        PayPalAccountAffirmed = true,
        CreatorAgreementAccepted = true,
        LocationCertification = CreatorLocationCertification.USPerson,
        AcknowledgmentAccepted = true,
        PayoutRequirementsAcknowledged = true
    };

    [Test]
    public async Task StartOnboardingAsync_NewCreator_USPerson_ActivatesWithoutPayPalOrTaxWhenDeferred()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "new@test.com", Email = "new@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = null;
        input.PayPalAccountAffirmed = false;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TaxFormPending, Is.False);
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.IsIneligible, Is.False);

        // Verify creator was created in DB with correct state
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id);
        Assert.That(creator, Is.Not.Null);
        Assert.That(creator!.IsActive, Is.True);
        Assert.That(creator.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(creator.PayPalEmail, Is.Null);
        Assert.That(creator.PayPalAccountAffirmed, Is.False);
        Assert.That(creator.CreatorAgreementAccepted, Is.True);
        Assert.That(creator.CreatorAgreementAcceptedAtUtc, Is.Not.Null);
        Assert.That(creator.LocationCertification, Is.EqualTo(CreatorLocationCertification.USPerson));
        Assert.That(creator.AcknowledgmentAccepted, Is.True);
        Assert.That(creator.PayoutRequirementsAcknowledged, Is.True);
        Assert.That(creator.PayoutRequirementsAcknowledgedAtUtc, Is.Not.Null);
        Assert.That(creator.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted));
        Assert.That(creator.DisplayName, Is.EqualTo("Test Creator"));
        Assert.That(creator.Bio, Is.EqualTo("Test bio"));
        _mockUserManager.Verify(x => x.AddToRoleAsync(user, Roles.Creator), Times.Once);
        _mockCreatorEmailService.Verify(
            x => x.SendCreatorWelcomeEmailAsync(user.Email!, It.IsAny<string>(), false, false),
            Times.Once);
    }

    [Test]
    public async Task StartOnboardingAsync_NewCreator_NonUSPersonOutsideUS_Activates()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "foreign@test.com", Email = "foreign@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.LocationCertification = CreatorLocationCertification.NonUSPersonOutsideUS;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TaxFormPending, Is.False);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_NonUSPersonInsideUS_ActivatesBecauseAgreementOnly()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "ineligible@test.com", Email = "ineligible@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.LocationCertification = CreatorLocationCertification.NonUSPersonInsideUS;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsIneligible, Is.False);
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.TaxFormPending, Is.False);

        // Verify legacy location data is preserved without blocking activation
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id);
        Assert.That(creator!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(creator.IsActive, Is.True);
        Assert.That(creator.LocationCertification, Is.EqualTo(CreatorLocationCertification.NonUSPersonInsideUS));
    }

    [Test]
    public async Task StartOnboardingAsync_ReturningCreator_WithCompletedTaxForm_ActivatesImmediately()
    {
        // Arrange — creator who previously stopped selling but has completed tax form
        var user = new ApplicationUser { UserName = "returning@test.com", Email = "returning@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            TaxFormStatus = TaxFormStatus.Completed,
            TaxFormCompletedAt = DateTime.UtcNow.AddMonths(-3),
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(false);
        _mockUserManager.Setup(x => x.AddToRoleAsync(user, Roles.Creator)).ReturnsAsync(IdentityResult.Success);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = "new@paypal.com";

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.TaxFormPending, Is.False);
        Assert.That(result.IsIneligible, Is.False);

        // Verify role was assigned
        _mockUserManager.Verify(x => x.AddToRoleAsync(user, Roles.Creator), Times.Once);

        // Verify creator is active in DB
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(savedCreator!.IsActive, Is.True);
        Assert.That(savedCreator.PayPalEmail, Is.EqualTo("new@paypal.com"));
    }

    [Test]
    public async Task StartOnboardingAsync_ReturningCreator_WithoutCompletedTaxForm_ActivatesWithoutTaxPending()
    {
        // Arrange — creator who previously stopped selling, tax form NOT completed
        var user = new ApplicationUser { UserName = "return2@test.com", Email = "return2@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            TaxFormStatus = TaxFormStatus.NotStarted,
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TaxFormPending, Is.False);
        Assert.That(result.IsActive, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(savedCreator!.TaxFormStatus, Is.EqualTo(TaxFormStatus.NotStarted));
        Assert.That(savedCreator.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_SubmitTaxFormNow_SetsTaxFormPendingAndStillActivates()
    {
        var user = new ApplicationUser { UserName = "taxnow@test.com", Email = "taxnow@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.SubmitTaxFormNow = true;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.TaxFormPending, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstAsync(c => c.UserId == user.Id);
        Assert.That(creator.IsActive, Is.True);
        Assert.That(creator.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
        Assert.That(creator.TaxBanditsPayeeRef, Is.EqualTo(user.Email));
    }

    [Test]
    public async Task StartOnboardingAsync_AlreadyActiveCreator_ReturnsError()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "active@test.com", Email = "active@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            PayPalEmail = "active@paypal.com",
            PayPalAccountAffirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("already an active creator"));
    }

    [Test]
    public async Task StartOnboardingAsync_EmptyPayPalEmail_ActivatesWhenNotAffirmed()
    {
        var user = new ApplicationUser { UserName = "nopaypal@test.com", Email = "nopaypal@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = "";
        input.PayPalAccountAffirmed = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstAsync(c => c.UserId == user.Id);
        Assert.That(creator.PayPalEmail, Is.Null);
        Assert.That(creator.PayPalAccountAffirmed, Is.False);
    }

    [Test]
    public async Task StartOnboardingAsync_WhitespacePayPalEmail_ActivatesWhenNotAffirmed()
    {
        var user = new ApplicationUser { UserName = "whitespacepaypal@test.com", Email = "whitespacepaypal@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = "   ";
        input.PayPalAccountAffirmed = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_PayPalNotAffirmed_ReturnsError()
    {
        var input = CreateValidOnboardingInput(1);
        input.PayPalAccountAffirmed = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("affirm"));
    }

    [Test]
    public async Task StartOnboardingAsync_PayPalAffirmedWithoutEmail_ReturnsError()
    {
        var input = CreateValidOnboardingInput(1);
        input.PayPalEmail = null;
        input.PayPalAccountAffirmed = true;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("PayPal payout email"));
    }

    [Test]
    public async Task StartOnboardingAsync_InvalidPayPalEmail_ReturnsError()
    {
        var input = CreateValidOnboardingInput(1);
        input.PayPalEmail = "@angelaomalley72";
        input.PayPalAccountAffirmed = true;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(PayoutEmailValidator.InvalidPayPalEmailMessage));
    }

    [Test]
    public async Task StartOnboardingAsync_LocationCertificationNone_Activates()
    {
        var user = new ApplicationUser { UserName = "noloc@test.com", Email = "noloc@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.LocationCertification = CreatorLocationCertification.None;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_AcknowledgmentNotAccepted_ActivatesWhenAgreementAccepted()
    {
        var user = new ApplicationUser { UserName = "noack@test.com", Email = "noack@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.AcknowledgmentAccepted = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_PayoutRequirementsNotAcknowledged_ActivatesWhenAgreementAccepted()
    {
        var user = new ApplicationUser { UserName = "nopayoutack@test.com", Email = "nopayoutack@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayoutRequirementsAcknowledged = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task StartOnboardingAsync_CreatorAgreementNotAccepted_ReturnsError()
    {
        var input = CreateValidOnboardingInput(1);
        input.CreatorAgreementAccepted = false;
        input.AcknowledgmentAccepted = false;

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Creator Agreement"));
    }

    [Test]
    public async Task StartOnboardingAsync_UserNotFound_ReturnsError()
    {
        _mockUserManager.Setup(x => x.FindByIdAsync("999")).ReturnsAsync((ApplicationUser)null!);

        var input = CreateValidOnboardingInput(999);

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("email"));
    }

    [Test]
    public async Task StartOnboardingAsync_UserWithNoEmail_ReturnsError()
    {
        var user = new ApplicationUser { UserName = "noemail@test.com", Email = null };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id);

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("email"));
    }

    [Test]
    public async Task StartOnboardingAsync_UserWithEmptyEmail_ReturnsError()
    {
        var user = new ApplicationUser { UserName = "empty@test.com", Email = "  " };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id);

        var result = await _service.StartOnboardingAsync(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("email"));
    }

    [Test]
    public async Task StartOnboardingAsync_ExistingCreator_UpdatesProfile()
    {
        // Arrange — creator who was ineligible, now re-signing up with new info
        var user = new ApplicationUser { UserName = "profile@test.com", Email = "profile@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Ineligible,
            IsActive = false,
            DisplayName = "Old Name",
            Bio = "Old bio"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.DisplayName = "New Name";
        input.Bio = "New bio";

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);

        // Verify profile was updated
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(savedCreator!.DisplayName, Is.EqualTo("New Name"));
        Assert.That(savedCreator.Bio, Is.EqualTo("New bio"));
    }

    [Test]
    public async Task StartOnboardingAsync_ExistingCreator_NoDisplayName_DoesNotOverwriteProfile()
    {
        // Arrange — creator re-signing up without providing new display name
        var user = new ApplicationUser { UserName = "keepname@test.com", Email = "keepname@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Ineligible,
            IsActive = false,
            DisplayName = "Existing Name",
            Bio = "Existing bio"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.DisplayName = null;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert — profile should not be overwritten
        Assert.That(result.Success, Is.True);
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var savedCreator = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(savedCreator!.DisplayName, Is.EqualTo("Existing Name"));
    }

    [Test]
    public async Task StartOnboardingAsync_ReturningCreator_AlreadyHasRole_DoesNotAddAgain()
    {
        // Arrange — returning creator who already has the Creator role
        var user = new ApplicationUser { UserName = "hasrole@test.com", Email = "hasrole@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            TaxFormStatus = TaxFormStatus.Completed,
            TaxFormCompletedAt = DateTime.UtcNow.AddMonths(-1),
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(true); // Already has role

        var input = CreateValidOnboardingInput(user.Id, user.Email!);

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
        _mockUserManager.Verify(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), Roles.Creator), Times.Never);
    }

    [Test]
    public async Task StartOnboardingAsync_PayeeRef_IsStoredFromUserEmail()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "payeeref@test.com", Email = "payeeref@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = "different@paypal.com"; // PayPal email different from user email
        input.SubmitTaxFormNow = true;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TaxFormPending, Is.True);

        // Verify PayeeRef is stored from user email (not PayPal email)
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id);
        Assert.That(creator!.TaxBanditsPayeeRef, Is.EqualTo("payeeref@test.com"));
        Assert.That(creator.PayPalEmail, Is.EqualTo("different@paypal.com"));
    }

    [Test]
    public async Task StartOnboardingAsync_ConsentRevokedCreator_CanReSignUp()
    {
        // Arrange — creator whose consent was revoked
        var user = new ApplicationUser { UserName = "revoked@test.com", Email = "revoked@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.ConsentRevoked,
            IsActive = false,
            PayPalEmail = "old@paypal.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TaxFormPending, Is.False);
        Assert.That(result.IsActive, Is.True);
    }

    #endregion

    #region CompleteOnboardingAsync Tests

    [Test]
    public async Task CompleteOnboardingAsync_NoCreatorRecord_ReturnsError()
    {
        // Arrange — user who never started onboarding
        var user = new ApplicationUser { UserName = "nocreator@test.com", Email = "nocreator@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Creator record not found"));
    }

    [Test]
    public async Task CompleteOnboardingAsync_PayPalNotAffirmed_StillActivatesWhenCertificationsComplete()
    {
        // Arrange — creator without PayPal affirmation
        var user = new ApplicationUser { UserName = "noaffirm@test.com", Email = "noaffirm@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = false,
            IsActive = false,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task CompleteOnboardingAsync_BothComplete_ActivatesCreatorAndAssignsRole()
    {
        // Arrange — creator with both onboarding and tax form completed
        var user = new ApplicationUser { UserName = "ready@test.com", Email = "ready@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = true,
            PayPalEmail = "ready@paypal.com",
            IsActive = false,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(false);
        _mockUserManager.Setup(x => x.AddToRoleAsync(user, Roles.Creator)).ReturnsAsync(IdentityResult.Success);

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
        _mockUserManager.Verify(x => x.AddToRoleAsync(user, Roles.Creator), Times.Once);

        // Verify activation in DB
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.IsActive, Is.True);
    }

    [Test]
    public async Task CompleteOnboardingAsync_AlreadyActive_DoesNotReactivate()
    {
        // Arrange — already active creator
        var user = new ApplicationUser { UserName = "alreadyact@test.com", Email = "alreadyact@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = true,
            IsActive = true,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(true);

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
        _mockUserManager.Verify(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), Roles.Creator), Times.Never);
    }

    [Test]
    public async Task CompleteOnboardingAsync_OnboardingNotCompleted_ReturnsCurrentStatus()
    {
        // Arrange — creator with onboarding in progress but tax form not complete
        var user = new ApplicationUser { UserName = "pending@test.com", Email = "pending@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.InProgress,
            TaxFormStatus = TaxFormStatus.Pending,
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = false,
            IsActive = false,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert — returns status without error, not fully onboarded
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.False);
        Assert.That(result.PaymentsReceivable, Is.True);
        Assert.That(result.PrimaryEmailConfirmed, Is.False);
    }

    [Test]
    public async Task CompleteOnboardingAsync_TaxFormNotCompleted_StillActivates()
    {
        // Arrange — creator with completed onboarding but pending tax form
        var user = new ApplicationUser { UserName = "notax@test.com", Email = "notax@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            PayPalAccountAffirmed = true,
            IsActive = false,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    [Test]
    public async Task CompleteOnboardingAsync_FailedTaxForm_StillActivates()
    {
        // Arrange — creator whose tax form failed
        var user = new ApplicationUser { UserName = "failedtax@test.com", Email = "failedtax@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Failed,
            PayPalAccountAffirmed = true,
            IsActive = false,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CompleteOnboardingAsync(user.Id);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsActive, Is.True);
    }

    #endregion

    #region InitiateTaxFormUpdateAsync Tests

    [Test]
    public async Task InitiateTaxFormUpdateAsync_ActiveCreator_Success()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "taxupdate@test.com", Email = "taxupdate@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            PayPalEmail = "taxupdate@paypal.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, "taxupdate@test.com");

        // Assert
        Assert.That(result.Success, Is.True);

        // Verify tax form status set to Pending in DB
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
        Assert.That(saved.TaxBanditsPayeeRef, Is.EqualTo("taxupdate@test.com"));
    }

    [Test]
    public async Task InitiateTaxFormUpdateAsync_NoCreatorRecord_ReturnsError()
    {
        // Arrange — user is not a creator
        var user = new ApplicationUser { UserName = "notcreator@test.com", Email = "notcreator@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, user.Email);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("active creator"));
    }

    [Test]
    public async Task InitiateTaxFormUpdateAsync_InactiveCreator_ReturnsError()
    {
        // Arrange — creator who stopped selling
        var user = new ApplicationUser { UserName = "inactive@test.com", Email = "inactive@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, user.Email);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("active creator"));
    }

    [Test]
    public async Task InitiateTaxFormUpdateAsync_NullEmail_SkipsPayeeRefUpdate()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "nullemail@test.com", Email = "nullemail@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            TaxBanditsPayeeRef = "original@test.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, null);

        // Assert
        Assert.That(result.Success, Is.True);

        // Verify PayeeRef was NOT overwritten
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
        Assert.That(saved.TaxBanditsPayeeRef, Is.EqualTo("original@test.com"));
    }

    [Test]
    public async Task InitiateTaxFormUpdateAsync_EmptyEmail_SkipsPayeeRefUpdate()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "emptyeml@test.com", Email = "emptyeml@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            TaxBanditsPayeeRef = "existing@test.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, "   ");

        // Assert
        Assert.That(result.Success, Is.True);
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.TaxBanditsPayeeRef, Is.EqualTo("existing@test.com"));
    }

    #endregion

    #region Full End-to-End Orchestration Tests

    [Test]
    public async Task FullFlow_NewCreator_Onboard_CompleteTaxForm_Activate()
    {
        // This test simulates the complete flow a new user would go through:
        // 1. StartOnboarding -> creates and activates creator, optionally sets TaxFormPending
        // 2. (Tax form completed externally via webhook — simulate directly)
        // 3. CompleteOnboarding remains idempotent after tax completion

        // Arrange
        var user = new ApplicationUser { UserName = "e2e@test.com", Email = "e2e@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(false);
        _mockUserManager.Setup(x => x.AddToRoleAsync(user, Roles.Creator)).ReturnsAsync(IdentityResult.Success);

        // Step 1: Start onboarding
        var onboardingInput = CreateValidOnboardingInput(user.Id, user.Email!);
        onboardingInput.SubmitTaxFormNow = true;
        var startResult = await _service.StartOnboardingAsync(onboardingInput);
        Assert.That(startResult.Success, Is.True);
        Assert.That(startResult.IsActive, Is.True);
        Assert.That(startResult.TaxFormPending, Is.True);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(true);

        // Step 2: Simulate tax form completion (webhook calls this)
        var creatorAfterStart = await _service.GetCreatorByUserIdAsync(user.Id);
        await _service.UpdateTaxFormStatusAsync(creatorAfterStart!.Id, TaxFormStatus.Completed);

        // Step 3: Complete onboarding
        var completeResult = await _service.CompleteOnboardingAsync(user.Id);
        Assert.That(completeResult.Success, Is.True);
        Assert.That(completeResult.IsActive, Is.True);

        // Verify final state
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var finalCreator = await verifyContext.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id);
        Assert.That(finalCreator!.IsActive, Is.True);
        Assert.That(finalCreator.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(finalCreator.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed));
        Assert.That(finalCreator.PayPalEmail, Is.EqualTo("paypal@test.com"));
        Assert.That(finalCreator.PayPalAccountAffirmed, Is.True);

        _mockUserManager.Verify(x => x.AddToRoleAsync(user, Roles.Creator), Times.Once);
    }

    [Test]
    public async Task FullFlow_Creator_StopSelling_ReSignUp_WithCompletedTaxForm()
    {
        // Full flow: active creator stops selling, then re-signs up
        // Since they have TaxFormCompletedAt, they should be immediately re-activated

        // Arrange — active creator
        var user = new ApplicationUser { UserName = "fullflow2@test.com", Email = "fullflow2@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            TaxFormCompletedAt = DateTime.UtcNow.AddMonths(-6),
            IsActive = true,
            PayPalEmail = "original@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // UserManager mocks — first for StopBeingCreator (remove role), then for StartOnboarding (add role)
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator))
            .ReturnsAsync(true); // First call: has role
        _mockUserManager.Setup(x => x.RemoveFromRoleAsync(user, Roles.Creator)).ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.AddToRoleAsync(user, Roles.Creator)).ReturnsAsync(IdentityResult.Success);

        // Step 1: Stop selling
        var stopResult = await _service.StopBeingCreatorAsync(user.Id);
        Assert.That(stopResult, Is.True);

        // After stop, IsInRoleAsync should return false
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, Roles.Creator)).ReturnsAsync(false);

        // Step 2: Re-sign up
        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.PayPalEmail = "new@paypal.com";
        var startResult = await _service.StartOnboardingAsync(input);

        // Assert — should be immediately active (tax form already completed)
        Assert.That(startResult.Success, Is.True);
        Assert.That(startResult.IsActive, Is.True);
        Assert.That(startResult.TaxFormPending, Is.False);

        // Verify final DB state
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var finalCreator = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(finalCreator!.IsActive, Is.True);
        Assert.That(finalCreator.PayPalEmail, Is.EqualTo("new@paypal.com"));
    }

    [Test]
    public async Task FullFlow_Creator_UpdateTaxForm_WhileActive()
    {
        // Creator wants to update their tax form (e.g., address changed)

        // Arrange
        var user = new ApplicationUser { UserName = "taxchange@test.com", Email = "taxchange@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            PayPalEmail = "taxchange@paypal.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.InitiateTaxFormUpdateAsync(user.Id, user.Email);

        // Assert
        Assert.That(result.Success, Is.True);

        // Verify — creator is still active, but tax form is now pending
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.IsActive, Is.True, "Creator should remain active during tax form update");
        Assert.That(saved.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
    }

    [Test]
    public async Task FullFlow_LegacyIneligibleLocation_ActivatesBecauseAgreementOnly()
    {
        // Legacy non-US-inside-US location data is preserved, but no longer blocks creator activation.

        // Arrange
        var user = new ApplicationUser { UserName = "blocked@test.com", Email = "blocked@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var input = CreateValidOnboardingInput(user.Id, user.Email!);
        input.LocationCertification = CreatorLocationCertification.NonUSPersonInsideUS;

        // Act
        var result = await _service.StartOnboardingAsync(input);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.IsIneligible, Is.False);
        Assert.That(result.IsActive, Is.True);

        var completeResult = await _service.CompleteOnboardingAsync(user.Id);
        Assert.That(completeResult.IsActive, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var creator = await verifyContext.Creators.FirstAsync(c => c.UserId == user.Id);
        Assert.That(creator.LocationCertification, Is.EqualTo(CreatorLocationCertification.NonUSPersonInsideUS));
        Assert.That(creator.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
    }

    #endregion

    #region UpdateTaxFormStatusAsync Error Message Tests

    [Test]
    public async Task UpdateTaxFormStatusAsync_StoresErrorMessage_WhenStatusIsPending()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, TaxFormStatus = TaxFormStatus.TinMatchInProgress };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var errorMessage = "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).";

        // Act
        var result = await _service.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending, errorMessage);

        // Assert
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
        Assert.That(result.LastTaxFormErrorMessage, Is.EqualTo(errorMessage));

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.LastTaxFormErrorMessage, Is.EqualTo(errorMessage));
    }

    [Test]
    public async Task UpdateTaxFormStatusAsync_ClearsErrorMessage_WhenStatusIsCompleted()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Previous error"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Completed);

        // Assert
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed));
        Assert.That(result.LastTaxFormErrorMessage, Is.Null);
        Assert.That(result.TaxFormCompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task UpdateTaxFormStatusAsync_ClearsErrorMessage_WhenStatusIsTinMatchInProgress()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Previous error"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.TinMatchInProgress);

        // Assert
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.TinMatchInProgress));
        Assert.That(result.LastTaxFormErrorMessage, Is.Null);
    }

    [Test]
    public async Task UpdateTaxFormStatusAsync_ClearsErrorMessage_WhenPendingWithNoError()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Old error"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act — set to Pending with no error message (normal re-send, not error retry)
        var result = await _service.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);

        // Assert — error cleared because errorMessage defaults to null
        Assert.That(result.TaxFormStatus, Is.EqualTo(TaxFormStatus.Pending));
        Assert.That(result.LastTaxFormErrorMessage, Is.Null);
    }

    [Test]
    public async Task UpdateTaxFormStatusAsync_TruncatesErrorMessage_WhenExceedsMaxLength()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, TaxFormStatus = TaxFormStatus.TinMatchInProgress };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Build a message that exceeds the column max length
        var longMessage = new string('E', Creator.LastTaxFormErrorMessageMaxLength + 500);
        Assert.That(longMessage.Length, Is.GreaterThan(Creator.LastTaxFormErrorMessageMaxLength));

        // Act — should not throw
        Creator result = null!;
        Assert.DoesNotThrowAsync(async () =>
        {
            result = await _service.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending, longMessage);
        });

        // Assert — stored value is capped at the column limit
        Assert.That(result.LastTaxFormErrorMessage, Has.Length.EqualTo(Creator.LastTaxFormErrorMessageMaxLength));
        Assert.That(result.LastTaxFormErrorMessage, Is.EqualTo(longMessage[..Creator.LastTaxFormErrorMessageMaxLength]));

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.LastTaxFormErrorMessage, Has.Length.EqualTo(Creator.LastTaxFormErrorMessageMaxLength));
    }

    #endregion

    #region DeleteCreatorSongAsync Cleanup Tests

    [Test]
    public async Task DeleteCreatorSongAsync_RemovesSongFromUserPlaylists()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        var playlist = new Playlist { UserId = user.Id, PlaylistName = "My Playlist" };
        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();

        _context.UserPlaylists.Add(new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            SongMetadataId = song.Id
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteCreatorSongAsync(song.Id, creator.Id);

        // Assert
        Assert.That(result, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var playlistEntry = await verifyContext.UserPlaylists
            .Where(up => up.SongMetadataId == song.Id)
            .FirstOrDefaultAsync();
        Assert.That(playlistEntry, Is.Null, "Song should be removed from user playlists when creator deletes it");
    }

    [Test]
    public async Task DeleteCreatorSongAsync_RemovesSongFromRecommendedPlaylists()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        _context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = user.Id,
            SongMetadataId = song.Id,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow,
            Score = 5.0
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteCreatorSongAsync(song.Id, creator.Id);

        // Assert
        Assert.That(result, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var recommendedEntry = await verifyContext.RecommendedPlaylists
            .Where(rp => rp.SongMetadataId == song.Id)
            .FirstOrDefaultAsync();
        Assert.That(recommendedEntry, Is.Null, "Song should be removed from recommended playlists when creator deletes it");
    }

    #endregion

    private class TestDbContextFactory : IDbContextFactory<AppDbContext>
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
