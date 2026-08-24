using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The metadata a creator sets at the review step has to survive the gap between upload and
/// publication - staging, a queue message, transcoding in an Azure Function, then assembly here,
/// minutes later on a different machine. Nothing on the upload page is around by then.
/// </summary>
[TestFixture]
public class SongPublishMetadataFlowTests
{
    private SqliteConnection _connection = default!;
    private DbContextOptions<AppDbContext> _options = default!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public void SongPublishMetadata_None_CarriesNothing()
    {
        // Every caller outside the upload page passes this, so it must not quietly disclose AI use
        // or credit a persona nobody chose.
        var none = SongPublishMetadata.None;

        Assert.Multiple(() =>
        {
            Assert.That(none.Genre, Is.Null);
            Assert.That(none.PersonaId, Is.Null);
            Assert.That(none.IsAiGenerated, Is.False);
            Assert.That(none.IsAiVocals, Is.False);
            Assert.That(none.IsAiLyrics, Is.False);
        });
    }

    [Test]
    public async Task AJobRoundTripsEveryFieldThroughTheDatabase()
    {
        // The job row is the only thing that exists between the creator pressing Upload and the
        // song being assembled, so anything it drops is gone for good.
        var mediaGuid = Guid.NewGuid();
        var creatorId = await AddCreatorAsync();

        await using (var write = new AppDbContext(_options))
        {
            write.SongUploadJobs.Add(new SongUploadJob
            {
                MediaGuid = mediaGuid,
                CreatorId = creatorId,
                SongTitle = "Long Way Down",
                SourceBlobPath = "staging/a.wav",
                SourceFileName = "a.wav",
                SourceExtension = ".wav",
                SourceContentType = "audio/wav",
                Genre = "Alt Rock",
                PersonaId = 11,
                IsAiGenerated = true,
                IsAiLyrics = true,
            });
            await write.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_options);
        var job = await read.SongUploadJobs.SingleAsync(j => j.MediaGuid == mediaGuid);

        Assert.Multiple(() =>
        {
            Assert.That(job.Genre, Is.EqualTo("Alt Rock"));
            Assert.That(job.PersonaId, Is.EqualTo(11));
            Assert.That(job.IsAiGenerated, Is.True);
            Assert.That(job.IsAiLyrics, Is.True);
            Assert.That(job.IsAiVocals, Is.False, "an unset flag must not become set in transit");
        });
    }

    private async Task<int> AddCreatorAsync()
    {
        await using var context = new AppDbContext(_options);

        var user = new ApplicationUser { UserName = "flow@test.com", Email = "flow@test.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator { UserId = user.Id, DisplayName = "Flow" };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        return creator.Id;
    }

    [Test]
    public async Task SongMetadataPersonaId_IsARealForeignKey_SoADeletedPersonaWouldFailTheInsert()
    {
        // This is the reason MediaProcessingCompletionService resolves the persona instead of
        // copying it across. If this ever stops throwing, that guard can go - but while it throws,
        // an unguarded assembly would lose an upload the creator had already waited minutes for.
        var creatorId = await AddCreatorAsync();
        await using var context = new AppDbContext(_options);

        context.SongMetadata.Add(new SongMetadata
        {
            MediaGuid = Guid.NewGuid(),
            SongTitle = "Orphaned Persona",
            CreatorId = creatorId,
            Mp3BlobPath = "media/x.mp3",
            TrackLength = 120,
            PersonaId = 9999,
        });

        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }
}
