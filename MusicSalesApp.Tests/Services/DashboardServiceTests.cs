using Microsoft.EntityFrameworkCore;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class DashboardServiceTests
{
    private IDbContextFactory<AppDbContext> _contextFactory;
    private DashboardService _service;
    private DbContextOptions<AppDbContext> _contextOptions;
    private AppDbContext _context;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"DashboardTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        var mockFactory = new Moq.Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _contextFactory = mockFactory.Object;
        _service = new DashboardService(_contextFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<(Creator creator, SongMetadata song)> SeedCreatorAndSong()
    {
        using var context = new AppDbContext(_contextOptions);

        var user = new ApplicationUser
        {
            UserName = "creator@test.com",
            Email = "creator@test.com",
            NormalizedUserName = "CREATOR@TEST.COM",
            NormalizedEmail = "CREATOR@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true
        };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var song = new SongMetadata
        {
            CreatorId = creator.Id,
            Mp3BlobPath = "test/song.mp3"
        };
        context.SongMetadata.Add(song);
        await context.SaveChangesAsync();

        return (creator, song);
    }

    private async Task AddStreams(int creatorId, int songMetadataId, params DateTime[] dates)
    {
        using var context = new AppDbContext(_contextOptions);
        foreach (var date in dates)
        {
            context.SongStreams.Add(new SongStream
            {
                SongMetadataId = songMetadataId,
                CreatorId = creatorId,
                CreatedDate = date
            });
        }
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task GetStreamDataAsync_ReturnsEmptyList_WhenNoStreams()
    {
        var (creator, song) = await SeedCreatorAndSong();

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            DateTime.UtcNow.AddDays(-7),
            DateTime.UtcNow,
            StreamInterval.Day);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.All(dp => dp.StreamCount == 0), Is.True);
    }

    [Test]
    public async Task GetStreamDataAsync_GroupsByDay()
    {
        var (creator, song) = await SeedCreatorAndSong();
        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        await AddStreams(creator.Id, song.Id,
            baseDate.AddHours(2),
            baseDate.AddHours(5),
            baseDate.AddDays(1).AddHours(3));

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(2),
            StreamInterval.Day);

        var day1 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate);
        var day2 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate.AddDays(1));

        Assert.That(day1, Is.Not.Null);
        Assert.That(day1.StreamCount, Is.EqualTo(2));
        Assert.That(day2, Is.Not.Null);
        Assert.That(day2.StreamCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetStreamDataAsync_GroupsByHour()
    {
        var (creator, song) = await SeedCreatorAndSong();
        var baseDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        await AddStreams(creator.Id, song.Id,
            baseDate.AddMinutes(5),
            baseDate.AddMinutes(30),
            baseDate.AddHours(1).AddMinutes(15));

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            baseDate,
            baseDate.AddHours(2),
            StreamInterval.Hour);

        var hour1 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate);
        var hour2 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate.AddHours(1));

        Assert.That(hour1, Is.Not.Null);
        Assert.That(hour1.StreamCount, Is.EqualTo(2));
        Assert.That(hour2, Is.Not.Null);
        Assert.That(hour2.StreamCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetStreamDataAsync_GroupsByMonth()
    {
        var (creator, song) = await SeedCreatorAndSong();

        await AddStreams(creator.Id, song.Id,
            new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            StreamInterval.Month);

        var jan = result.FirstOrDefault(dp => dp.PeriodStart == new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var feb = result.FirstOrDefault(dp => dp.PeriodStart == new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(jan, Is.Not.Null);
        Assert.That(jan.StreamCount, Is.EqualTo(2));
        Assert.That(feb, Is.Not.Null);
        Assert.That(feb.StreamCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetStreamDataAsync_GroupsByYear()
    {
        var (creator, song) = await SeedCreatorAndSong();

        await AddStreams(creator.Id, song.Id,
            new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            StreamInterval.Year);

        var y2024 = result.FirstOrDefault(dp => dp.PeriodStart == new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var y2025 = result.FirstOrDefault(dp => dp.PeriodStart == new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(y2024, Is.Not.Null);
        Assert.That(y2024.StreamCount, Is.EqualTo(1));
        Assert.That(y2025, Is.Not.Null);
        Assert.That(y2025.StreamCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetStreamDataAsync_FillsMissingPeriodsWithZero()
    {
        var (creator, song) = await SeedCreatorAndSong();
        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Only add stream on day 1 (day 2 and 3 should be zero)
        await AddStreams(creator.Id, song.Id, baseDate.AddHours(2));

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(2),
            StreamInterval.Day);

        Assert.That(result.Count, Is.EqualTo(3)); // 3 days
        Assert.That(result[0].StreamCount, Is.EqualTo(1));
        Assert.That(result[1].StreamCount, Is.EqualTo(0));
        Assert.That(result[2].StreamCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetStreamDataAsync_OnlyReturnsStreamsForSpecifiedCreator()
    {
        var (creator1, song1) = await SeedCreatorAndSong();

        // Create a second creator with a different user
        using var context = new AppDbContext(_contextOptions);
        var user2 = new ApplicationUser
        {
            UserName = "creator2@test.com",
            Email = "creator2@test.com",
            NormalizedUserName = "CREATOR2@TEST.COM",
            NormalizedEmail = "CREATOR2@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user2);
        await context.SaveChangesAsync();

        var creator2 = new Creator { UserId = user2.Id, IsActive = true };
        context.Creators.Add(creator2);
        await context.SaveChangesAsync();

        var song2 = new SongMetadata { CreatorId = creator2.Id, Mp3BlobPath = "test/song2.mp3" };
        context.SongMetadata.Add(song2);
        await context.SaveChangesAsync();

        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Add streams for both creators
        context.SongStreams.Add(new SongStream { SongMetadataId = song1.Id, CreatorId = creator1.Id, CreatedDate = baseDate.AddHours(1) });
        context.SongStreams.Add(new SongStream { SongMetadataId = song2.Id, CreatorId = creator2.Id, CreatedDate = baseDate.AddHours(2) });
        await context.SaveChangesAsync();

        // Query only creator1's data
        var result = await _service.GetStreamDataAsync(
            creator1.Id,
            baseDate,
            baseDate.AddDays(1),
            StreamInterval.Day);

        var day1 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate);
        Assert.That(day1, Is.Not.Null);
        Assert.That(day1.StreamCount, Is.EqualTo(1)); // Only creator1's stream
    }

    [Test]
    public async Task GetStreamDataAsync_GroupsByWeek()
    {
        var (creator, song) = await SeedCreatorAndSong();

        // Monday Jan 13, 2025
        var week1Start = new DateTime(2025, 1, 13, 0, 0, 0, DateTimeKind.Utc);

        await AddStreams(creator.Id, song.Id,
            week1Start.AddDays(1),  // Tuesday of week 1
            week1Start.AddDays(3),  // Thursday of week 1
            week1Start.AddDays(8)); // Tuesday of week 2

        var result = await _service.GetStreamDataAsync(
            creator.Id,
            week1Start,
            week1Start.AddDays(14),
            StreamInterval.Week);

        var firstWeek = result.FirstOrDefault(dp => dp.PeriodStart == week1Start);
        Assert.That(firstWeek, Is.Not.Null);
        Assert.That(firstWeek.StreamCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetStreamDataAsync_FiltersStreamsByGenre()
    {
        using var context = new AppDbContext(_contextOptions);

        var user = new ApplicationUser
        {
            UserName = "genrecreator@test.com",
            Email = "genrecreator@test.com",
            NormalizedUserName = "GENRECREATOR@TEST.COM",
            NormalizedEmail = "GENRECREATOR@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true, User = user };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var rockSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/rock.mp3", Genre = "Rock" };
        var popSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/pop.mp3", Genre = "Pop" };
        context.SongMetadata.AddRange(rockSong, popSong);
        await context.SaveChangesAsync();

        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        context.SongStreams.AddRange(
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(1) },
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(2) },
            new SongStream { SongMetadataId = popSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(3) }
        );
        await context.SaveChangesAsync();

        // Filter by Rock genre only
        var result = await _service.GetStreamDataAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(1),
            StreamInterval.Day,
            genres: new HashSet<string> { "Rock" });

        var day1 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate);
        Assert.That(day1, Is.Not.Null);
        Assert.That(day1.StreamCount, Is.EqualTo(2)); // Only Rock streams
    }

    [Test]
    public async Task GetStreamDataAsync_FiltersStreamsBySongTitle()
    {
        using var context = new AppDbContext(_contextOptions);

        var user = new ApplicationUser
        {
            UserName = "titlecreator@test.com",
            Email = "titlecreator@test.com",
            NormalizedUserName = "TITLECREATOR@TEST.COM",
            NormalizedEmail = "TITLECREATOR@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true, User = user };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var song1 = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/song1.mp3", SongTitle = "Awesome Song" };
        var song2 = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/song2.mp3", SongTitle = "Other Song" };
        context.SongMetadata.AddRange(song1, song2);
        await context.SaveChangesAsync();

        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        context.SongStreams.AddRange(
            new SongStream { SongMetadataId = song1.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(1) },
            new SongStream { SongMetadataId = song2.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(2) },
            new SongStream { SongMetadataId = song2.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(3) }
        );
        await context.SaveChangesAsync();

        // Filter by "Other Song" title only
        var result = await _service.GetStreamDataAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(1),
            StreamInterval.Day,
            songTitles: new HashSet<string> { "Other Song" });

        var day1 = result.FirstOrDefault(dp => dp.PeriodStart == baseDate);
        Assert.That(day1, Is.Not.Null);
        Assert.That(day1.StreamCount, Is.EqualTo(2)); // Only "Other Song" streams
    }

    [Test]
    public async Task GetStreamFilterOptionsAsync_ReturnsOnlyStreamedSongs()
    {
        using var context = new AppDbContext(_contextOptions);

        var user = new ApplicationUser
        {
            UserName = "filtercreator@test.com",
            Email = "filtercreator@test.com",
            NormalizedUserName = "FILTERCREATOR@TEST.COM",
            NormalizedEmail = "FILTERCREATOR@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true, User = user };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var rockSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/rock.mp3", Genre = "Rock", SongTitle = "Rock Hit", ArtistName = "RockBand" };
        var popSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/pop.mp3", Genre = "Pop", SongTitle = "Pop Hit", ArtistName = "PopStar" };
        var jazzSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/jazz.mp3", Genre = "Jazz", SongTitle = "Jazz Tune", ArtistName = "JazzCat" };
        context.SongMetadata.AddRange(rockSong, popSong, jazzSong);
        await context.SaveChangesAsync();

        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Only add streams for Rock and Pop songs — Jazz has no streams
        context.SongStreams.AddRange(
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(1) },
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(2) },
            new SongStream { SongMetadataId = popSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(3) }
        );
        await context.SaveChangesAsync();

        var result = await _service.GetStreamFilterOptionsAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(1));

        // Jazz should NOT appear since it has no streams
        Assert.That(result.Genres, Does.ContainKey("Rock"));
        Assert.That(result.Genres, Does.ContainKey("Pop"));
        Assert.That(result.Genres, Does.Not.ContainKey("Jazz"));
        Assert.That(result.Genres["Rock"], Is.EqualTo(2));
        Assert.That(result.Genres["Pop"], Is.EqualTo(1));

        Assert.That(result.Artists, Does.ContainKey("RockBand"));
        Assert.That(result.Artists, Does.ContainKey("PopStar"));
        Assert.That(result.Artists, Does.Not.ContainKey("JazzCat"));

        Assert.That(result.SongTitles, Does.ContainKey("Rock Hit"));
        Assert.That(result.SongTitles, Does.ContainKey("Pop Hit"));
        Assert.That(result.SongTitles, Does.Not.ContainKey("Jazz Tune"));
    }

    [Test]
    public async Task GetStreamFilterOptionsAsync_CrossFiltersWhenGenreSelected()
    {
        using var context = new AppDbContext(_contextOptions);

        var user = new ApplicationUser
        {
            UserName = "crossfilter@test.com",
            Email = "crossfilter@test.com",
            NormalizedUserName = "CROSSFILTER@TEST.COM",
            NormalizedEmail = "CROSSFILTER@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, IsActive = true, User = user };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var rockSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/rock.mp3", Genre = "Rock", SongTitle = "Rock Hit", ArtistName = "RockBand" };
        var popSong = new SongMetadata { CreatorId = creator.Id, Mp3BlobPath = "test/pop.mp3", Genre = "Pop", SongTitle = "Pop Hit", ArtistName = "PopStar" };
        context.SongMetadata.AddRange(rockSong, popSong);
        await context.SaveChangesAsync();

        var baseDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        context.SongStreams.AddRange(
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(1) },
            new SongStream { SongMetadataId = rockSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(2) },
            new SongStream { SongMetadataId = popSong.Id, CreatorId = creator.Id, CreatedDate = baseDate.AddHours(3) }
        );
        await context.SaveChangesAsync();

        // Select "Rock" genre — artists and song titles should only show Rock items
        var result = await _service.GetStreamFilterOptionsAsync(
            creator.Id,
            baseDate,
            baseDate.AddDays(1),
            selectedGenres: new HashSet<string> { "Rock" });

        // Genres should still show both (genre filter doesn't filter itself)
        Assert.That(result.Genres, Does.ContainKey("Rock"));
        Assert.That(result.Genres, Does.ContainKey("Pop"));

        // Artists should only show RockBand (cross-filtered by selected genre)
        Assert.That(result.Artists, Does.ContainKey("RockBand"));
        Assert.That(result.Artists, Does.Not.ContainKey("PopStar"));

        // Song titles should only show Rock Hit (cross-filtered by selected genre)
        Assert.That(result.SongTitles, Does.ContainKey("Rock Hit"));
        Assert.That(result.SongTitles, Does.Not.ContainKey("Pop Hit"));
    }
}
