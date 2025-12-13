using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PlaylistCleanupServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<PlaylistCleanupService>> _mockLogger;
    private PlaylistCleanupService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        // Create in-memory database for testing
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"PlaylistCleanupTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockLogger = new Mock<ILogger<PlaylistCleanupService>>();
        
        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new PlaylistCleanupService(_mockContextFactory.Object, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task RemoveNonOwnedSongsFromLapsedSubscriptionsAsync_NoLapsedSubscriptions_ReturnsZero()
    {
        // Arrange - Create a user with an active subscription
        var user = new ApplicationUser { Id = 1, UserName = "test@example.com", Email = "test@example.com" };
        _context.Users.Add(user);
        
        var activeSubscription = new Subscription
        {
            UserId = user.Id,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow.AddDays(-30),
            EndDate = DateTime.UtcNow.AddDays(30),
            MonthlyPrice = 9.99m
        };
        _context.Subscriptions.Add(activeSubscription);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        // Assert
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task RemoveNonOwnedSongsFromLapsedSubscriptionsAsync_LapsedSubscriptionWithNonOwnedSongs_RemovesSongs()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "test@example.com", Email = "test@example.com" };
        _context.Users.Add(user);
        
        // Create a lapsed subscription (ended more than 48 hours ago)
        var lapsedSubscription = new Subscription
        {
            UserId = user.Id,
            Status = "CANCELLED",
            StartDate = DateTime.UtcNow.AddDays(-60),
            EndDate = DateTime.UtcNow.AddHours(-49), // More than 48 hours ago
            CancelledAt = DateTime.UtcNow.AddDays(-10),
            MonthlyPrice = 9.99m
        };
        _context.Subscriptions.Add(lapsedSubscription);

        // Create a playlist
        var playlist = new Playlist
        {
            UserId = user.Id,
            PlaylistName = "My Playlist"
        };
        _context.Playlists.Add(playlist);

        // Create owned songs - one purchased, one subscription-only
        var purchasedSong = new OwnedSong
        {
            UserId = user.Id,
            SongFileName = "purchased-song.mp3",
            PayPalOrderId = "ORDER-123", // This song was purchased
            PurchasedAt = DateTime.UtcNow.AddDays(-30)
        };
        
        var subscriptionSong = new OwnedSong
        {
            UserId = user.Id,
            SongFileName = "subscription-song.mp3",
            PayPalOrderId = null, // This song was only accessible via subscription
            PurchasedAt = DateTime.UtcNow.AddDays(-30)
        };
        
        _context.OwnedSongs.AddRange(purchasedSong, subscriptionSong);
        await _context.SaveChangesAsync();

        // Add both songs to playlist
        var userPlaylist1 = new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            OwnedSongId = purchasedSong.Id,
            AddedAt = DateTime.UtcNow.AddDays(-20)
        };
        
        var userPlaylist2 = new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            OwnedSongId = subscriptionSong.Id,
            AddedAt = DateTime.UtcNow.AddDays(-20)
        };
        
        _context.UserPlaylists.AddRange(userPlaylist1, userPlaylist2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        // Assert
        Assert.That(result, Is.EqualTo(1)); // Should remove only the subscription-only song
        
        // Verify the purchased song is still in the playlist
        var remainingSongs = await _context.UserPlaylists.ToListAsync();
        Assert.That(remainingSongs.Count, Is.EqualTo(1));
        Assert.That(remainingSongs[0].OwnedSongId, Is.EqualTo(purchasedSong.Id));
    }

    [Test]
    public async Task RemoveNonOwnedSongsFromLapsedSubscriptionsAsync_UserWithNewActiveSubscription_KeepsSongs()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "test@example.com", Email = "test@example.com" };
        _context.Users.Add(user);
        
        // Create an old lapsed subscription
        var oldSubscription = new Subscription
        {
            UserId = user.Id,
            Status = "CANCELLED",
            StartDate = DateTime.UtcNow.AddDays(-60),
            EndDate = DateTime.UtcNow.AddHours(-49),
            CancelledAt = DateTime.UtcNow.AddDays(-10),
            MonthlyPrice = 9.99m
        };
        
        // Create a new active subscription
        var newSubscription = new Subscription
        {
            UserId = user.Id,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow.AddDays(-5),
            EndDate = DateTime.UtcNow.AddDays(25),
            MonthlyPrice = 9.99m
        };
        
        _context.Subscriptions.AddRange(oldSubscription, newSubscription);

        // Create a playlist with a subscription-only song
        var playlist = new Playlist
        {
            UserId = user.Id,
            PlaylistName = "My Playlist"
        };
        _context.Playlists.Add(playlist);

        var subscriptionSong = new OwnedSong
        {
            UserId = user.Id,
            SongFileName = "subscription-song.mp3",
            PayPalOrderId = null,
            PurchasedAt = DateTime.UtcNow.AddDays(-30)
        };
        
        _context.OwnedSongs.Add(subscriptionSong);
        await _context.SaveChangesAsync();

        var userPlaylist = new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            OwnedSongId = subscriptionSong.Id,
            AddedAt = DateTime.UtcNow.AddDays(-20)
        };
        
        _context.UserPlaylists.Add(userPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        // Assert
        Assert.That(result, Is.EqualTo(0)); // Should not remove any songs due to active subscription
        
        var remainingSongs = await _context.UserPlaylists.ToListAsync();
        Assert.That(remainingSongs.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task RemoveNonOwnedSongsFromLapsedSubscriptionsAsync_Within48HourGracePeriod_KeepsSongs()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "test@example.com", Email = "test@example.com" };
        _context.Users.Add(user);
        
        // Create a subscription that ended less than 48 hours ago
        var recentlyLapsedSubscription = new Subscription
        {
            UserId = user.Id,
            Status = "CANCELLED",
            StartDate = DateTime.UtcNow.AddDays(-30),
            EndDate = DateTime.UtcNow.AddHours(-24), // Only 24 hours ago (within grace period)
            CancelledAt = DateTime.UtcNow.AddHours(-24),
            MonthlyPrice = 9.99m
        };
        _context.Subscriptions.Add(recentlyLapsedSubscription);

        // Create a playlist with a subscription-only song
        var playlist = new Playlist
        {
            UserId = user.Id,
            PlaylistName = "My Playlist"
        };
        _context.Playlists.Add(playlist);

        var subscriptionSong = new OwnedSong
        {
            UserId = user.Id,
            SongFileName = "subscription-song.mp3",
            PayPalOrderId = null,
            PurchasedAt = DateTime.UtcNow.AddDays(-30)
        };
        
        _context.OwnedSongs.Add(subscriptionSong);
        await _context.SaveChangesAsync();

        var userPlaylist = new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            OwnedSongId = subscriptionSong.Id,
            AddedAt = DateTime.UtcNow.AddDays(-20)
        };
        
        _context.UserPlaylists.Add(userPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        // Assert
        Assert.That(result, Is.EqualTo(0)); // Should not remove songs within grace period
        
        var remainingSongs = await _context.UserPlaylists.ToListAsync();
        Assert.That(remainingSongs.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task RemoveNonOwnedSongsFromLapsedSubscriptionsAsync_MultipleUsers_RemovesCorrectSongs()
    {
        // Arrange
        var user1 = new ApplicationUser { Id = 1, UserName = "user1@example.com", Email = "user1@example.com" };
        var user2 = new ApplicationUser { Id = 2, UserName = "user2@example.com", Email = "user2@example.com" };
        _context.Users.AddRange(user1, user2);

        // User 1: Lapsed subscription
        var lapsedSub = new Subscription
        {
            UserId = user1.Id,
            Status = "EXPIRED",
            StartDate = DateTime.UtcNow.AddDays(-60),
            EndDate = DateTime.UtcNow.AddHours(-50),
            MonthlyPrice = 9.99m
        };
        
        // User 2: Active subscription
        var activeSub = new Subscription
        {
            UserId = user2.Id,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow.AddDays(-10),
            EndDate = DateTime.UtcNow.AddDays(20),
            MonthlyPrice = 9.99m
        };
        
        _context.Subscriptions.AddRange(lapsedSub, activeSub);

        // Create playlists for both users
        var playlist1 = new Playlist { UserId = user1.Id, PlaylistName = "User 1 Playlist" };
        var playlist2 = new Playlist { UserId = user2.Id, PlaylistName = "User 2 Playlist" };
        _context.Playlists.AddRange(playlist1, playlist2);

        // Create subscription songs for both users
        var song1 = new OwnedSong { UserId = user1.Id, SongFileName = "song1.mp3", PayPalOrderId = null };
        var song2 = new OwnedSong { UserId = user2.Id, SongFileName = "song2.mp3", PayPalOrderId = null };
        _context.OwnedSongs.AddRange(song1, song2);
        await _context.SaveChangesAsync();

        // Add songs to playlists
        _context.UserPlaylists.AddRange(
            new UserPlaylist { UserId = user1.Id, PlaylistId = playlist1.Id, OwnedSongId = song1.Id },
            new UserPlaylist { UserId = user2.Id, PlaylistId = playlist2.Id, OwnedSongId = song2.Id }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        // Assert
        Assert.That(result, Is.EqualTo(1)); // Should remove only user1's song
        
        var remainingSongs = await _context.UserPlaylists.ToListAsync();
        Assert.That(remainingSongs.Count, Is.EqualTo(1));
        Assert.That(remainingSongs[0].UserId, Is.EqualTo(user2.Id));
    }
}
