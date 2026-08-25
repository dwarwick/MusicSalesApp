using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// SetLikeStateAsync against a real Sqlite database, so the unique (UserId, SongMetadataId) index on
/// SongLikes is actually enforced. The InMemory provider used by <see cref="SongLikeServiceTests"/>
/// silently ignores that index, so it cannot cover the duplicate-insert recovery path at all.
/// </summary>
[TestFixture]
public class SongLikeServiceSetStateConcurrencyTests
{
    private SqliteConnection _connection;
    private DbContextOptions<AppDbContext> _contextOptions;
    private AppDbContext _context;
    private SongLikeService _service;
    private int _userId;
    private int _songMetadataId;

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

        // Sqlite enforces the SongLike foreign keys that the InMemory provider ignores, so the
        // referenced user and song have to actually exist.
        // No explicit Id - EnsureCreated() applies the model's seed data, which already occupies low ids.
        var user = new ApplicationUser { UserName = "user1@test.com", Email = "user1@test.com" };
        var song = new SongMetadata { BlobPath = "test.mp3" };
        _context.Users.Add(user);
        _context.SongMetadata.Add(song);
        _context.SaveChanges();
        _userId = user.Id;
        _songMetadataId = song.Id;

        var mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        var mockHubContext = new Mock<IHubContext<LikeCountHub>>();
        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _service = new SongLikeService(mockContextFactory.Object, mockHubContext.Object, SongLikeServiceConfiguration.RequireStream(false));
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task SetLikeStateAsync_RepeatedIdenticalCalls_LeaveExactlyOneRow()
    {
        for (var i = 0; i < 5; i++)
            await _service.SetLikeStateAsync(_userId, _songMetadataId, true);

        var rows = await LoadRowsAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].IsLike, Is.True);
    }

    [Test]
    public async Task SetLikeStateAsync_ConcurrentIdenticalCalls_DoNotThrowAndLeaveOneRow()
    {
        // Recovery path: whichever caller loses the race against the unique index must re-read and
        // reapply rather than surfacing a DbUpdateException to the client.
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _service.SetLikeStateAsync(_userId, _songMetadataId, true)));

        var rows = await LoadRowsAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].IsLike, Is.True);
    }

    [Test]
    public async Task SetLikeStateAsync_ClearAfterConcurrentSets_RemovesTheRow()
    {
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => _service.SetLikeStateAsync(_userId, _songMetadataId, true)));

        await _service.SetLikeStateAsync(_userId, _songMetadataId, null);

        Assert.That(await LoadRowsAsync(), Is.Empty);
    }

    [Test]
    public async Task SetLikeStateAsync_ForASongThatDoesNotExist_ThrowsSongNotFound()
    {
        // Must be distinguishable from a transient write failure. The mobile client retries anything it
        // reads as transient forever, and its flush stops at the first failure, so an intent that can
        // never be written would block every intent queued behind it.
        await Assert.ThatAsync(() => _service.SetLikeStateAsync(_userId, songMetadataId: 999_999, state: true),
            Throws.InstanceOf<SongNotFoundException>());
    }

    [Test]
    public async Task SetLikeStateAsync_ForASongDeletedAfterItWasQueued_ThrowsSongNotFound()
    {
        // The realistic path: the app snapshots the catalog, goes offline, and the song is deleted
        // before the queued thumbs-up is replayed.
        await using (var deletingContext = new AppDbContext(_contextOptions))
        {
            var song = await deletingContext.SongMetadata.FindAsync(_songMetadataId);
            deletingContext.SongMetadata.Remove(song!);
            await deletingContext.SaveChangesAsync();
        }

        await Assert.ThatAsync(() => _service.SetLikeStateAsync(_userId, _songMetadataId, state: true),
            Throws.InstanceOf<SongNotFoundException>());
    }

    [Test]
    public async Task ToggleLikeAsync_ForASongThatDoesNotExist_ThrowsSongNotFound()
    {
        // The toggles share the write path, so they report a deleted song the same way rather than
        // letting a raw DbUpdateException become a 500.
        await Assert.ThatAsync(() => _service.ToggleLikeAsync(_userId, songMetadataId: 999_999),
            Throws.InstanceOf<SongNotFoundException>());
    }

    [Test]
    public async Task ToggleDislikeAsync_ForASongThatDoesNotExist_ThrowsSongNotFound()
    {
        await Assert.ThatAsync(() => _service.ToggleDislikeAsync(_userId, songMetadataId: 999_999),
            Throws.InstanceOf<SongNotFoundException>());
    }

    [Test]
    public async Task ToggleLikeAsync_ConcurrentCalls_DoNotThrowAndLeaveAtMostOneRow()
    {
        // A double-tap used to be able to lose the race against the unique index and surface a raw
        // DbUpdateException. The shared write path recovers instead.
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _service.ToggleLikeAsync(_userId, _songMetadataId)));

        Assert.That(await LoadRowsAsync(), Has.Count.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task ToggleDislikeAsync_ConcurrentCalls_DoNotThrowAndLeaveAtMostOneRow()
    {
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _service.ToggleDislikeAsync(_userId, _songMetadataId)));

        Assert.That(await LoadRowsAsync(), Has.Count.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task SetLikeStateAsync_WithAnUnrelatedWriteFailure_StillSurfacesTheOriginalException()
    {
        // The song exists, so this is not a SongNotFoundException - the user foreign key is what fails.
        // The duplicate-insert recovery must not disguise that as success, nor relabel it.
        await Assert.ThatAsync(() => _service.SetLikeStateAsync(userId: 999_999, _songMetadataId, state: true),
            Throws.InstanceOf<DbUpdateException>());
    }

    private async Task<List<SongLike>> LoadRowsAsync()
    {
        using var verifyContext = new AppDbContext(_contextOptions);
        return await verifyContext.SongLikes
            .Where(sl => sl.UserId == _userId && sl.SongMetadataId == _songMetadataId)
            .ToListAsync();
    }
}
