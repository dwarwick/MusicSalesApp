using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// A real Sqlite database plus the handful of rows every follow test needs.
/// </summary>
/// <remarks>
/// Sqlite rather than the InMemory provider, because most of what is worth testing here IS the
/// schema: the unique index that makes a double follow impossible, the filtered unique index that
/// allows one thank-you per follower, and the foreign keys. InMemory ignores all three, so a test
/// against it would pass whether or not those constraints existed.
/// </remarks>
public sealed class ArtistFollowTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public DbContextOptions<AppDbContext> Options { get; }
    public Mock<IDbContextFactory<AppDbContext>> ContextFactory { get; }

    public int CreatorUserId { get; private set; }
    public int CreatorId { get; private set; }
    public int PersonaId { get; private set; }
    public int ListenerUserId { get; private set; }
    public int SongId { get; private set; }

    /// <summary>
    /// When the seeded song went public. Set well in the past so it behaves like back catalogue -
    /// after the AddArtistFollowFeature migration every existing row carries a past date, so a
    /// harness whose only song looked freshly released would not resemble any real database.
    /// </summary>
    public static readonly DateTime SeededSongPublishedAtUtc =
        new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public ArtistFollowTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var context = NewContext())
        {
            context.Database.EnsureCreated();
            Seed(context);
        }

        ContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        ContextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(Options));
        ContextFactory
            .Setup(factory => factory.CreateDbContext())
            .Returns(() => new AppDbContext(Options));
    }

    public AppDbContext NewContext() => new(Options);

    private void Seed(AppDbContext context)
    {
        // Sqlite enforces the foreign keys the InMemory provider ignores, so every referenced row
        // has to exist. Ids are assigned by the database - EnsureCreated applies the model's seed
        // data, which already occupies the low ones.
        var creatorUser = new ApplicationUser
        {
            UserName = "creator@test.com",
            Email = "creator@test.com",
            EmailConfirmed = true,
        };

        var listenerUser = new ApplicationUser
        {
            UserName = "listener@test.com",
            Email = "listener@test.com",
            EmailConfirmed = true,
        };

        context.Users.AddRange(creatorUser, listenerUser);
        context.SaveChanges();

        CreatorUserId = creatorUser.Id;
        ListenerUserId = listenerUser.Id;

        var creator = new Creator
        {
            UserId = creatorUser.Id,
            DisplayName = "Creator",
            IsActive = true,
        };

        context.Creators.Add(creator);
        context.SaveChanges();
        CreatorId = creator.Id;

        var persona = new CreatorPersona
        {
            CreatorId = creator.Id,
            Name = "Alex Rivers",
            IsEnabled = true,
        };

        context.CreatorPersonas.Add(persona);
        context.SaveChanges();
        PersonaId = persona.Id;

        var song = new SongMetadata
        {
            SongTitle = "Midnight Highway",
            Mp3BlobPath = "songs/midnight-highway.mp3",
            BlobPath = "songs/midnight-highway.mp3",
            PersonaId = persona.Id,
            CreatorId = creator.Id,
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            CreatedAt = SeededSongPublishedAtUtc,
            FirstPublishedAtUtc = SeededSongPublishedAtUtc,
        };

        context.SongMetadata.Add(song);
        context.SaveChanges();
        SongId = song.Id;
    }

    /// <summary>
    /// Adds a second listener, for tests that need more than one follower.
    /// </summary>
    public int AddListener(string email)
    {
        using var context = NewContext();

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        context.Users.Add(user);
        context.SaveChanges();

        return user.Id;
    }

    /// <summary>
    /// Adds another song for the seeded persona, optionally already stamped as published.
    /// </summary>
    /// <summary>
    /// Turns the listener's two follow-feature email preferences on.
    /// </summary>
    /// <remarks>
    /// Both default to OFF, matching <c>ReceiveNewSongEmails</c> beside them, so a test that
    /// expects an email has to say that the listener asked for one. Following an artist is
    /// consent to the in-app record, not consent to be mailed.
    /// </remarks>
    public async Task OptListenerIntoEmailsAsync()
    {
        await using var context = NewContext();

        var listener = await context.Users.SingleAsync(u => u.Id == ListenerUserId);
        listener.ReceiveArtistReleaseEmails = true;
        listener.ReceiveArtistMessageEmails = true;

        await context.SaveChangesAsync();
    }

    public int AddSong(string title, DateTime? firstPublishedAtUtc = null, bool isEnabled = true)
    {
        using var context = NewContext();

        var song = new SongMetadata
        {
            SongTitle = title,
            Mp3BlobPath = $"songs/{title}.mp3",
            BlobPath = $"songs/{title}.mp3",
            PersonaId = PersonaId,
            CreatorId = CreatorId,
            IsActive = true,
            IsEnabled = isEnabled,
            IsAlbumCover = false,
            FirstPublishedAtUtc = firstPublishedAtUtc,
        };

        context.SongMetadata.Add(song);
        context.SaveChanges();

        return song.Id;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
