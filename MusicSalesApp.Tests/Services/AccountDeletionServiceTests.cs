using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AccountDeletionServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ICreatorService> _mockCreatorService;
    private Mock<ICreatorPersonaService> _mockCreatorPersonaService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<ILogger<AccountDeletionService>> _mockLogger;
    private Mock<IAppleTokenRevocationService> _mockAppleTokenRevocationService;
    private AccountDeletionService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;
    private SqliteConnection _connection;

    private const int TestUserId = 100;
    private const int OtherUserId = 200;
    private const int TestCreatorId = 10;
    private const int OtherCreatorId = 20;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_contextOptions);
        _context.Database.EnsureCreated();

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockCreatorService = new Mock<ICreatorService>();
        _mockCreatorPersonaService = new Mock<ICreatorPersonaService>();
        _mockLogger = new Mock<ILogger<AccountDeletionService>>();

        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _mockUserManager.Setup(x => x.DeleteAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);

        // Default: user is not a creator
        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Creator)null!);

        _mockAppleTokenRevocationService = new Mock<IAppleTokenRevocationService>();
        _mockAppleTokenRevocationService.SetupGet(x => x.IsConfigured).Returns(true);
        _mockAppleTokenRevocationService
            .Setup(x => x.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new AccountDeletionService(
            _mockContextFactory.Object,
            _mockCreatorService.Object,
            _mockCreatorPersonaService.Object,
            _mockUserManager.Object,
            _mockLogger.Object,
            _mockAppleTokenRevocationService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _connection.Dispose();
    }

    private ApplicationUser CreateTestUser(int userId = TestUserId)
    {
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"user{userId}@test.com",
            Email = $"user{userId}@test.com"
        };
        _context.Users.Add(user);
        return user;
    }

    private SongMetadata CreateSongMetadata()
    {
        var song = new SongMetadata { BlobPath = "test.mp3" };
        _context.SongMetadata.Add(song);
        return song;
    }

    private Creator CreateCreator(int userId, int creatorId = TestCreatorId)
    {
        var creator = new Creator { Id = creatorId, UserId = userId };
        _context.Creators.Add(creator);
        return creator;
    }

    private Playlist CreatePlaylist(int userId, string name = "Test Playlist")
    {
        var playlist = new Playlist { PlaylistName = name, UserId = userId };
        _context.Playlists.Add(playlist);
        return playlist;
    }

    #region Non-Creator User Tests

    [Test]
    public async Task DeleteAccountAsync_NonCreator_NullifiesSongStreamStreamerUserId()
    {
        // Arrange
        var user = CreateTestUser();
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = user.Id });
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = user.Id });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var streams = await verifyContext.SongStreams.ToListAsync();
        Assert.That(streams, Has.Count.EqualTo(2));
        Assert.That(streams.All(s => s.StreamerUserId == null), Is.True,
            "All SongStream.StreamerUserId should be nullified");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DeletesUserPlaylists()
    {
        // Arrange
        var user = CreateTestUser();
        var song = CreateSongMetadata();
        var playlist = CreatePlaylist(user.Id);
        await _context.SaveChangesAsync();

        _context.UserPlaylists.Add(new UserPlaylist { UserId = user.Id, PlaylistId = playlist.Id, SongMetadataId = song.Id });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var playlists = await verifyContext.UserPlaylists.Where(up => up.UserId == user.Id).ToListAsync();
        Assert.That(playlists, Is.Empty, "All UserPlaylist rows for the user should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DeletesTipsAsTipper()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(otherUser.Id, OtherCreatorId);
        await _context.SaveChangesAsync();

        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = creator.Id, Amount = 5.00m, PayPalOrderId = "ORDER1" });
        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = creator.Id, Amount = 10.00m, PayPalOrderId = "ORDER2" });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.Where(t => t.TipperUserId == user.Id).ToListAsync();
        Assert.That(tips, Is.Empty, "All Tips where user is tipper should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DeletesBlockedTipAttemptsAsTipper()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(otherUser.Id, OtherCreatorId);
        await _context.SaveChangesAsync();

        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = user.Id, CreatorId = creator.Id, Amount = 5.00m,
            FraudRule = "Rule1", Reason = "Blocked"
        });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var blocked = await verifyContext.BlockedTipAttempts.Where(b => b.TipperUserId == user.Id).ToListAsync();
        Assert.That(blocked, Is.Empty, "All BlockedTipAttempts where user is tipper should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DeletesReportedSongs()
    {
        // Arrange
        var user = CreateTestUser();
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        _context.ReportedSongs.Add(new ReportedSong
        {
            SongMetadataId = song.Id,
            ReportingUserId = user.Id,
            Reason = ReportReasonTypes.TermsOfUseViolation
        });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var reports = await verifyContext.ReportedSongs.Where(r => r.ReportingUserId == user.Id).ToListAsync();
        Assert.That(reports, Is.Empty, "All ReportedSong rows for the user should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DeletesMobileVerificationCodes()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        _context.MobileVerificationCodes.Add(new MobileVerificationCode
        {
            UserId = user.Id,
            Code = "123456",
            Purpose = MobileVerificationPurpose.EmailVerification,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var codes = await verifyContext.MobileVerificationCodes.Where(code => code.UserId == user.Id).ToListAsync();
        Assert.That(codes, Is.Empty, "All MobileVerificationCode rows for the user should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreator_DoesNotAffectOtherUsersRecords()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var song = CreateSongMetadata();
        var creator = CreateCreator(otherUser.Id, OtherCreatorId);
        var playlist = CreatePlaylist(otherUser.Id);
        await _context.SaveChangesAsync();

        // User's records
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = user.Id });
        _context.UserPlaylists.Add(new UserPlaylist { UserId = user.Id, PlaylistId = playlist.Id, SongMetadataId = song.Id });
        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = creator.Id, Amount = 5.00m, PayPalOrderId = "O1" });
        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = user.Id, CreatorId = creator.Id, Amount = 1.00m,
            FraudRule = "R", Reason = "X"
        });

        // Other user's records (should NOT be affected)
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = otherUser.Id });
        var otherPlaylist = CreatePlaylist(otherUser.Id, "Other Playlist");
        await _context.SaveChangesAsync();
        _context.UserPlaylists.Add(new UserPlaylist { UserId = otherUser.Id, PlaylistId = otherPlaylist.Id, SongMetadataId = song.Id });
        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 3.00m, PayPalOrderId = "O2" });
        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 2.00m,
            FraudRule = "R2", Reason = "Y"
        });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);

        var otherStreams = await verifyContext.SongStreams.Where(s => s.StreamerUserId == otherUser.Id).ToListAsync();
        Assert.That(otherStreams, Has.Count.EqualTo(1), "Other user's SongStreams should not be affected");

        var otherUserPlaylists = await verifyContext.UserPlaylists.Where(up => up.UserId == otherUser.Id).ToListAsync();
        Assert.That(otherUserPlaylists, Has.Count.EqualTo(1), "Other user's UserPlaylists should not be affected");

        var otherTips = await verifyContext.Tips.Where(t => t.TipperUserId == otherUser.Id).ToListAsync();
        Assert.That(otherTips, Has.Count.EqualTo(1), "Other user's Tips should not be affected");

        var otherBlocked = await verifyContext.BlockedTipAttempts.Where(b => b.TipperUserId == otherUser.Id).ToListAsync();
        Assert.That(otherBlocked, Has.Count.EqualTo(1), "Other user's BlockedTipAttempts should not be affected");
    }

    #endregion

    #region Creator User Tests

    [Test]
    public async Task DeleteAccountAsync_Creator_NullifiesSongStreamCreatorId()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, CreatorId = creator.Id });
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, CreatorId = creator.Id, StreamerUserId = otherUser.Id });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var streams = await verifyContext.SongStreams.ToListAsync();
        Assert.That(streams, Has.Count.EqualTo(2));
        Assert.That(streams.All(s => s.CreatorId == null), Is.True,
            "All SongStream.CreatorId should be nullified for the creator");
    }

    [Test]
    public async Task DeleteAccountAsync_Creator_DeletesTipsAsCreator()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        await _context.SaveChangesAsync();

        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 15.00m, PayPalOrderId = "O1" });
        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 20.00m, PayPalOrderId = "O2" });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.Where(t => t.CreatorId == creator.Id).ToListAsync();
        Assert.That(tips, Is.Empty, "All Tips where user is creator should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_Creator_DeletesBlockedTipAttemptsAsCreator()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        await _context.SaveChangesAsync();

        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 5.00m,
            FraudRule = "Rule1", Reason = "Blocked"
        });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var blocked = await verifyContext.BlockedTipAttempts.Where(b => b.CreatorId == creator.Id).ToListAsync();
        Assert.That(blocked, Is.Empty, "All BlockedTipAttempts where user is creator should be deleted");
    }

    [Test]
    public async Task DeleteAccountAsync_Creator_CallsDeleteAllPersonas()
    {
        // Arrange
        var user = CreateTestUser();
        var creator = CreateCreator(user.Id);
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);
        _mockCreatorPersonaService.Setup(x => x.DeleteAllPersonasForCreatorAsync(creator.Id))
            .ReturnsAsync(3);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        _mockCreatorPersonaService.Verify(
            x => x.DeleteAllPersonasForCreatorAsync(creator.Id), Times.Once,
            "Should call DeleteAllPersonasForCreatorAsync for the creator");
    }

    [Test]
    public async Task DeleteAccountAsync_ActiveCreator_ReturnsFailureAndDoesNotDeleteUser()
    {
        // Arrange
        var user = CreateTestUser();
        var creator = CreateCreator(user.Id);
        creator.IsActive = true;
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        var result = await _service.DeleteAccountAsync(user);

        // Assert
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors.Single().Code, Is.EqualTo(AccountDeletionErrorCodes.ActiveCreatorMustStopSellingFirst));
        _mockUserManager.Verify(x => x.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Test]
    public async Task DeleteAccountAsync_Creator_DoesNotAffectOtherCreatorsRecords()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        var otherCreator = CreateCreator(otherUser.Id, OtherCreatorId);
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        // Creator's records
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, CreatorId = creator.Id });
        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 5.00m, PayPalOrderId = "O1" });
        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 1.00m,
            FraudRule = "R", Reason = "X"
        });

        // Other creator's records (should NOT be affected)
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, CreatorId = otherCreator.Id });
        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = otherCreator.Id, Amount = 3.00m, PayPalOrderId = "O2" });
        _context.BlockedTipAttempts.Add(new BlockedTipAttempt
        {
            TipperUserId = user.Id, CreatorId = otherCreator.Id, Amount = 2.00m,
            FraudRule = "R2", Reason = "Y"
        });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);

        var otherCreatorStreams = await verifyContext.SongStreams.Where(s => s.CreatorId == otherCreator.Id).ToListAsync();
        Assert.That(otherCreatorStreams, Has.Count.EqualTo(1), "Other creator's SongStreams should not be affected");

        // Note: other creator's tips where user was tipper are deleted in the non-creator cleanup phase
        // But tips where someone ELSE tipped the other creator should remain
        var otherCreatorBlocked = await verifyContext.BlockedTipAttempts.Where(b => b.CreatorId == otherCreator.Id).ToListAsync();
        // The user's BlockedTipAttempt as tipper for otherCreator is deleted in the non-creator cleanup
        Assert.That(otherCreatorBlocked, Is.Empty,
            "BlockedTipAttempts where deleted user was tipper for other creator are cleaned up in tipper cleanup phase");
    }

    [Test]
    public async Task DeleteAccountAsync_CreatorAlsoStreamer_CleansUpBothRoles()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        var otherCreator = CreateCreator(otherUser.Id, OtherCreatorId);
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        // User as streamer
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = user.Id, CreatorId = otherCreator.Id });
        // User as creator
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, CreatorId = creator.Id, StreamerUserId = otherUser.Id });
        // Tips as tipper
        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = otherCreator.Id, Amount = 5.00m, PayPalOrderId = "O1" });
        // Tips as creator
        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 10.00m, PayPalOrderId = "O2" });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);

        var allStreams = await verifyContext.SongStreams.ToListAsync();
        Assert.That(allStreams, Has.Count.EqualTo(2), "Stream records should still exist (nullified, not deleted)");
        Assert.That(allStreams.All(s => s.StreamerUserId != user.Id), Is.True, "StreamerUserId should be nullified");
        Assert.That(allStreams.All(s => s.CreatorId != creator.Id), Is.True, "CreatorId should be nullified");

        var allTips = await verifyContext.Tips.ToListAsync();
        Assert.That(allTips, Is.Empty, "All tips involving the user as tipper or creator should be deleted");
    }

    #endregion

    #region Persona Deletion Failure Tests

    [Test]
    public async Task DeleteAccountAsync_PersonaDeletionThrows_StillDeletesUser()
    {
        // Arrange
        var user = CreateTestUser();
        var creator = CreateCreator(user.Id);
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);
        _mockCreatorPersonaService.Setup(x => x.DeleteAllPersonasForCreatorAsync(creator.Id))
            .ThrowsAsync(new InvalidOperationException("Blob storage unavailable"));

        // Act
        var result = await _service.DeleteAccountAsync(user);

        // Assert
        Assert.That(result.Succeeded, Is.True, "User deletion should succeed even if persona deletion fails");
        _mockUserManager.Verify(x => x.DeleteAsync(user), Times.Once);
    }

    [Test]
    public async Task DeleteAccountAsync_PersonaDeletionThrows_LogsError()
    {
        // Arrange
        var user = CreateTestUser();
        var creator = CreateCreator(user.Id);
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);
        _mockCreatorPersonaService.Setup(x => x.DeleteAllPersonasForCreatorAsync(creator.Id))
            .ThrowsAsync(new InvalidOperationException("Blob storage unavailable"));

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to delete creator personas")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    #endregion

    #region UserManager Result Tests

    [Test]
    public async Task DeleteAccountAsync_ReturnsIdentityResultSuccess()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(x => x.DeleteAsync(user))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var result = await _service.DeleteAccountAsync(user);

        // Assert
        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task DeleteAccountAsync_UserManagerFails_ReturnsFailureResult()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        var failedResult = IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "User was modified" });
        _mockUserManager.Setup(x => x.DeleteAsync(user))
            .ReturnsAsync(failedResult);

        // Act
        var result = await _service.DeleteAccountAsync(user);

        // Assert
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors.First().Code, Is.EqualTo("ConcurrencyFailure"));
    }

    [Test]
    public async Task DeleteAccountAsync_CallsUserManagerDeleteAsync()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        _mockUserManager.Verify(x => x.DeleteAsync(user), Times.Once);
    }

    #endregion

    #region No Related Records Tests

    [Test]
    public async Task DeleteAccountAsync_UserWithNoRelatedRecords_DeletesCleanly()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteAccountAsync(user);

        // Assert
        Assert.That(result.Succeeded, Is.True);
        _mockUserManager.Verify(x => x.DeleteAsync(user), Times.Once);
    }

    [Test]
    public async Task DeleteAccountAsync_NonCreatorUser_DoesNotCallPersonaDeletion()
    {
        // Arrange
        var user = CreateTestUser();
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        _mockCreatorPersonaService.Verify(
            x => x.DeleteAllPersonasForCreatorAsync(It.IsAny<int>()), Times.Never,
            "Should not call DeleteAllPersonasForCreatorAsync for non-creator user");
    }

    #endregion

    #region Multiple Records Tests

    [Test]
    public async Task DeleteAccountAsync_MultipleStreams_NullifiesAll()
    {
        // Arrange
        var user = CreateTestUser();
        var song1 = CreateSongMetadata();
        var song2 = CreateSongMetadata();
        await _context.SaveChangesAsync();

        for (int i = 0; i < 5; i++)
        {
            _context.SongStreams.Add(new SongStream { SongMetadataId = song1.Id, StreamerUserId = user.Id });
            _context.SongStreams.Add(new SongStream { SongMetadataId = song2.Id, StreamerUserId = user.Id });
        }
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var streams = await verifyContext.SongStreams.ToListAsync();
        Assert.That(streams, Has.Count.EqualTo(10));
        Assert.That(streams.All(s => s.StreamerUserId == null), Is.True);
    }

    [Test]
    public async Task DeleteAccountAsync_MultipleUserPlaylists_DeletesAll()
    {
        // Arrange
        var user = CreateTestUser();
        var song1 = CreateSongMetadata();
        var song2 = CreateSongMetadata();
        var song3 = CreateSongMetadata();
        var playlist1 = CreatePlaylist(user.Id, "Playlist 1");
        var playlist2 = CreatePlaylist(user.Id, "Playlist 2");
        await _context.SaveChangesAsync();

        _context.UserPlaylists.Add(new UserPlaylist { UserId = user.Id, PlaylistId = playlist1.Id, SongMetadataId = song1.Id });
        _context.UserPlaylists.Add(new UserPlaylist { UserId = user.Id, PlaylistId = playlist1.Id, SongMetadataId = song2.Id });
        _context.UserPlaylists.Add(new UserPlaylist { UserId = user.Id, PlaylistId = playlist2.Id, SongMetadataId = song3.Id });
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var playlists = await verifyContext.UserPlaylists.Where(up => up.UserId == user.Id).ToListAsync();
        Assert.That(playlists, Is.Empty);
    }

    [Test]
    public async Task DeleteAccountAsync_MultipleTips_DeletesAll()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(otherUser.Id, OtherCreatorId);
        await _context.SaveChangesAsync();

        for (int i = 0; i < 5; i++)
        {
            _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = creator.Id, Amount = i + 1.00m, PayPalOrderId = $"ORDER{i}" });
        }
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.Where(t => t.TipperUserId == user.Id).ToListAsync();
        Assert.That(tips, Is.Empty);
    }

    [Test]
    public async Task DeleteAccountAsync_MultipleBlockedAttempts_DeletesAll()
    {
        // Arrange
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(otherUser.Id, OtherCreatorId);
        await _context.SaveChangesAsync();

        for (int i = 0; i < 3; i++)
        {
            _context.BlockedTipAttempts.Add(new BlockedTipAttempt
            {
                TipperUserId = user.Id, CreatorId = creator.Id, Amount = i + 1.00m,
                FraudRule = $"Rule{i}", Reason = $"Reason{i}"
            });
        }
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var blocked = await verifyContext.BlockedTipAttempts.Where(b => b.TipperUserId == user.Id).ToListAsync();
        Assert.That(blocked, Is.Empty);
    }

    #endregion

    #region Creator Streamer Overlap Tests

    [Test]
    public async Task DeleteAccountAsync_CreatorStreamedOwnSongs_NullifiesBothFields()
    {
        // Arrange
        var user = CreateTestUser();
        var creator = CreateCreator(user.Id);
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        // User streamed their own song (both StreamerUserId and CreatorId point to the same user/creator)
        _context.SongStreams.Add(new SongStream { SongMetadataId = song.Id, StreamerUserId = user.Id, CreatorId = creator.Id });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var stream = await verifyContext.SongStreams.FirstAsync();
        Assert.That(stream.StreamerUserId, Is.Null, "StreamerUserId should be nullified");
        Assert.That(stream.CreatorId, Is.Null, "CreatorId should also be nullified");
    }

    [Test]
    public async Task DeleteAccountAsync_CreatorWithTipsInBothRoles_DeletesAll()
    {
        // Arrange: user is both a creator and a tipper (tipped another creator)
        var user = CreateTestUser();
        var otherUser = CreateTestUser(OtherUserId);
        var creator = CreateCreator(user.Id);
        var otherCreator = CreateCreator(otherUser.Id, OtherCreatorId);
        await _context.SaveChangesAsync();

        // User tipped another creator
        _context.Tips.Add(new Tip { TipperUserId = user.Id, CreatorId = otherCreator.Id, Amount = 5.00m, PayPalOrderId = "O1" });
        // Another user tipped the user (as creator)
        _context.Tips.Add(new Tip { TipperUserId = otherUser.Id, CreatorId = creator.Id, Amount = 10.00m, PayPalOrderId = "O2" });
        await _context.SaveChangesAsync();

        _mockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(user.Id))
            .ReturnsAsync(creator);

        // Act
        await _service.DeleteAccountAsync(user);

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var allTips = await verifyContext.Tips.ToListAsync();
        Assert.That(allTips, Is.Empty, "All tips involving the user in either role should be deleted");
    }

    #endregion
    [Test]
    public async Task DeleteAccountAsync_AppleUser_RevokesTheGrantBeforeDeleting()
    {
        // Apple requires the grant be revoked on account deletion, and the token is unrecoverable
        // once the row is gone - so this must happen before UserManager.DeleteAsync.
        var user = CreateTestUser();
        user.AppleRefreshToken = "apple-refresh-token";

        await _service.DeleteAccountAsync(user);

        _mockAppleTokenRevocationService.Verify(
            x => x.RevokeRefreshTokenAsync("apple-refresh-token", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DeleteAccountAsync_NonAppleUser_DoesNotCallApple()
    {
        var user = CreateTestUser();
        user.AppleRefreshToken = null;

        await _service.DeleteAccountAsync(user);

        _mockAppleTokenRevocationService.Verify(
            x => x.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeleteAccountAsync_WhenRevocationFails_StillDeletesTheAccount()
    {
        // A failure at Apple must not strand the user with an account they asked us to delete.
        var user = CreateTestUser();
        user.AppleRefreshToken = "apple-refresh-token";
        _mockAppleTokenRevocationService
            .Setup(x => x.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("appleid.apple.com unreachable"));

        var result = await _service.DeleteAccountAsync(user);

        Assert.That(result.Succeeded, Is.True);
        _mockUserManager.Verify(x => x.DeleteAsync(user), Times.Once);
    }

    [Test]
    public async Task DeleteAccountAsync_WhenRevocationNotConfigured_StillDeletesTheAccount()
    {
        _mockAppleTokenRevocationService.SetupGet(x => x.IsConfigured).Returns(false);
        var user = CreateTestUser();
        user.AppleRefreshToken = "apple-refresh-token";

        var result = await _service.DeleteAccountAsync(user);

        Assert.That(result.Succeeded, Is.True);
        _mockAppleTokenRevocationService.Verify(
            x => x.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The follow feature adds three tables whose listener-side foreign keys are all NoAction, so
    /// nothing removes them on its own. Leaving any behind would block the user row from being
    /// deleted at all - and if it somehow did not, would leave a deleted account sitting in
    /// creators' follower counts.
    /// </summary>
    [Test]
    public async Task DeleteAccountAsync_RemovesEveryFollowFeatureRowForTheUser()
    {
        var user = CreateTestUser();
        var creatorUser = CreateTestUser(TestUserId + 1);
        var song = CreateSongMetadata();
        await _context.SaveChangesAsync();

        var creator = CreateCreator(creatorUser.Id);
        await _context.SaveChangesAsync();

        var persona = new CreatorPersona { CreatorId = creator.Id, Name = "Alex Rivers" };
        _context.CreatorPersonas.Add(persona);
        await _context.SaveChangesAsync();

        var follow = new ArtistFollower
        {
            CreatorPersonaId = persona.Id,
            ListenerUserId = user.Id,
            FollowedDateUtc = DateTime.UtcNow,
            IsActive = true,
            AnonymousListenerNumber = 4817,
        };
        _context.ArtistFollowers.Add(follow);
        _context.ArtistReleaseNotifications.Add(new ArtistReleaseNotification
        {
            CreatorPersonaId = persona.Id,
            SongMetadataId = song.Id,
            ListenerUserId = user.Id,
            CreatedDateUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        _context.ArtistFollowerMessages.Add(new ArtistFollowerMessage
        {
            ArtistFollowerId = follow.Id,
            SenderUserId = creatorUser.Id,
            MessageKind = ArtistMessageKinds.ThankYou,
            MessageText = "Thanks!",
            CreatedDateUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _service.DeleteAccountAsync(user);

        await using var verifyContext = new AppDbContext(_contextOptions);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(await verifyContext.ArtistFollowers.AnyAsync(), Is.False);
            Assert.That(await verifyContext.ArtistReleaseNotifications.AnyAsync(), Is.False);

            // Cascaded away with the follow row it hung off.
            Assert.That(await verifyContext.ArtistFollowerMessages.AnyAsync(), Is.False);
        });
    }

    /// <summary>
    /// A creator's outgoing messages sit on OTHER people's follow relationships, so deleting their
    /// own follows does not reach them - and SenderUserId is NoAction, so the user row cannot go
    /// until they do.
    /// </summary>
    [Test]
    public async Task DeleteAccountAsync_RemovesMessagesTheUserSentAsACreator()
    {
        var creatorUser = CreateTestUser();
        var listener = CreateTestUser(TestUserId + 1);
        await _context.SaveChangesAsync();

        var creator = CreateCreator(creatorUser.Id);
        await _context.SaveChangesAsync();

        var persona = new CreatorPersona { CreatorId = creator.Id, Name = "Alex Rivers" };
        _context.CreatorPersonas.Add(persona);
        await _context.SaveChangesAsync();

        var follow = new ArtistFollower
        {
            CreatorPersonaId = persona.Id,
            ListenerUserId = listener.Id,
            FollowedDateUtc = DateTime.UtcNow,
            IsActive = true,
            AnonymousListenerNumber = 3012,
        };
        _context.ArtistFollowers.Add(follow);
        await _context.SaveChangesAsync();

        _context.ArtistFollowerMessages.Add(new ArtistFollowerMessage
        {
            ArtistFollowerId = follow.Id,
            SenderUserId = creatorUser.Id,
            MessageKind = ArtistMessageKinds.ThankYou,
            MessageText = "Thanks!",
            CreatedDateUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _service.DeleteAccountAsync(creatorUser);

        await using var verifyContext = new AppDbContext(_contextOptions);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(await verifyContext.ArtistFollowerMessages.AnyAsync(), Is.False);
        });
    }
}
