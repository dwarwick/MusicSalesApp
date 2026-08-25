using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The rule that a user must have streamed a song before they can rate it.
///
/// Enforced inside SongLikeService rather than in MusicController, because the Blazor app calls the
/// service in-process and never passes through the controller - so these tests cover the web app and the
/// mobile API at once.
/// </summary>
[TestFixture]
public class SongLikeServiceStreamRequirementTests
{
    private const int UserId = 7;
    private const int OtherUserId = 8;

    private DbContextOptions<AppDbContext> _contextOptions;
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IHubContext<LikeCountHub>> _mockHubContext;
    private AppDbContext _context;
    private int _songId;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"SongLikeStreamRuleDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockHubContext = new Mock<IHubContext<LikeCountHub>>();
        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _songId = SeedSong();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private int SeedSong()
    {
        var metadata = new SongMetadata
        {
            BlobPath = "test/song.mp3",
            Mp3BlobPath = "test/song.mp3",
            AlbumName = "Test Album",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.SongMetadata.Add(metadata);
        _context.SaveChanges();
        return metadata.Id;
    }

    private SongLikeService CreateService(bool requireStream = true) =>
        new(_mockContextFactory.Object,
            _mockHubContext.Object,
            SongLikeServiceConfiguration.RequireStream(requireStream));

    private SongLikeService CreateService(IConfiguration configuration) =>
        new(_mockContextFactory.Object, _mockHubContext.Object, configuration);

    private void SeedStream(int? streamerUserId, int? songMetadataId = null)
    {
        _context.SongStreams.Add(new SongStream
        {
            SongMetadataId = songMetadataId ?? _songId,
            StreamerUserId = streamerUserId,
            CreatedDate = DateTime.UtcNow
        });
        _context.SaveChanges();
    }

    private void SeedLike(bool isLike)
    {
        _context.SongLikes.Add(new SongLike
        {
            UserId = UserId,
            SongMetadataId = _songId,
            IsLike = isLike,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();
    }

    private int CountLikes() => new AppDbContext(_contextOptions).SongLikes.Count();

    // --- Setting an opinion requires a stream ---

    [Test]
    public void ToggleLikeAsync_WithoutStream_Throws()
    {
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.ToggleLikeAsync(UserId, _songId));
        Assert.That(CountLikes(), Is.Zero, "No like row should be written.");
    }

    [Test]
    public void ToggleDislikeAsync_WithoutStream_Throws()
    {
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.ToggleDislikeAsync(UserId, _songId));
        Assert.That(CountLikes(), Is.Zero);
    }

    [Test]
    public void SetLikeStateAsync_WithoutStream_Throws()
    {
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.SetLikeStateAsync(UserId, _songId, true));
        Assert.That(CountLikes(), Is.Zero);
    }

    [Test]
    public async Task SetLikeStateAsync_WithStream_Succeeds()
    {
        SeedStream(UserId);
        var service = CreateService();

        var result = await service.SetLikeStateAsync(UserId, _songId, true);

        Assert.That(result, Is.True);
        using var context = new AppDbContext(_contextOptions);
        Assert.That(context.SongLikes.Single(like => like.UserId == UserId).IsLike, Is.True);
    }

    [Test]
    public async Task ToggleLikeAsync_WithStream_Succeeds()
    {
        SeedStream(UserId);
        var service = CreateService();

        var isLiked = await service.ToggleLikeAsync(UserId, _songId);

        Assert.That(isLiked, Is.True);
    }

    // --- Whose stream, and of which song ---

    [Test]
    public void SetLikeStateAsync_StreamByAnotherUser_Throws()
    {
        SeedStream(OtherUserId);
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.SetLikeStateAsync(UserId, _songId, true));
    }

    [Test]
    public void SetLikeStateAsync_AnonymousStreamOfTheSameSong_Throws()
    {
        // A stream recorded while logged out carries a null StreamerUserId. It must not confer
        // eligibility on the account that later signs in, or anyone could rate anything by playing the
        // song first and registering afterwards.
        SeedStream(streamerUserId: null);
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.SetLikeStateAsync(UserId, _songId, true));
    }

    [Test]
    public void SetLikeStateAsync_StreamOfADifferentSong_Throws()
    {
        var otherSongId = SeedSong();
        SeedStream(UserId, otherSongId);
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.SetLikeStateAsync(UserId, _songId, true));
    }

    // --- Clearing an opinion is always allowed ---

    [Test]
    public async Task SetLikeStateAsync_ClearingWithoutStream_Succeeds()
    {
        // The rating predates the rule. The user must still be able to take it back.
        SeedLike(isLike: true);
        var service = CreateService();

        var result = await service.SetLikeStateAsync(UserId, _songId, null);

        Assert.That(result, Is.Null);
        Assert.That(CountLikes(), Is.Zero);
    }

    [Test]
    public async Task ToggleLikeAsync_RemovingAnExistingLikeWithoutStream_Succeeds()
    {
        SeedLike(isLike: true);
        var service = CreateService();

        var isLiked = await service.ToggleLikeAsync(UserId, _songId);

        Assert.That(isLiked, Is.False, "Toggling an active like off is a clear, which needs no stream.");
        Assert.That(CountLikes(), Is.Zero);
    }

    [Test]
    public void ToggleDislikeAsync_FlippingALikeToADislikeWithoutStream_Throws()
    {
        // Not a clear - this sets a new opinion, so the rule applies even though a row already exists.
        SeedLike(isLike: true);
        var service = CreateService();

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.ToggleDislikeAsync(UserId, _songId));

        using var context = new AppDbContext(_contextOptions);
        Assert.That(context.SongLikes.Single().IsLike, Is.True, "The existing like should be untouched.");
    }

    [Test]
    public async Task SetLikeStateAsync_AlreadyInTheRequestedStateWithoutStream_Succeeds()
    {
        // The idempotent no-op writes nothing, so it never reaches the rule. This matters for the mobile
        // offline queue, which replays intents that may already have been applied.
        SeedLike(isLike: true);
        var service = CreateService();

        var result = await service.SetLikeStateAsync(UserId, _songId, true);

        Assert.That(result, Is.True);
    }

    // --- The rollout flag ---

    [Test]
    public async Task SetLikeStateAsync_WithoutStream_SucceedsWhenEnforcementIsDisabled()
    {
        var service = CreateService(requireStream: false);

        var result = await service.SetLikeStateAsync(UserId, _songId, true);

        Assert.That(result, Is.True);
    }

    [Test]
    public void SetLikeStateAsync_WithoutStream_ThrowsWhenTheSettingIsAbsent()
    {
        // A fresh environment with no "Likes" section must be strict, not permissive.
        var service = CreateService(SongLikeServiceConfiguration.Empty());

        Assert.ThrowsAsync<LikeRequiresStreamException>(() => service.SetLikeStateAsync(UserId, _songId, true));
    }
}
