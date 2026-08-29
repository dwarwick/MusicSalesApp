using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The write tokens handed to a creator's browser.
///
/// <para>
/// This is the only place in the app that grants write access to storage from outside the server, so
/// the properties worth pinning are the ones that bound what a leaked or misused token can do: which
/// blob it names, which permissions it carries, and how long it lasts.
/// </para>
///
/// <para>
/// Entirely offline. SAS generation is a local HMAC over the account key, so a development-storage
/// connection string produces real, inspectable tokens with no Azurite running and no network.
/// </para>
/// </summary>
[TestFixture]
public class UploadStagingSasServiceTests
{
    private const string StagingContainer = "musicuploads-test";

    private static readonly Guid JobId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private MediaProcessingOptions _options = null!;
    private UploadStagingSasService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new MediaProcessingOptions();

        var containers = new Mock<IBlobContainerFactory>();
        containers
            .Setup(factory => factory.GetUploadStagingContainer())
            .Returns(new BlobContainerClient("UseDevelopmentStorage=true", StagingContainer));

        _service = new UploadStagingSasService(
            containers.Object,
            Options.Create(_options),
            Mock.Of<ILogger<UploadStagingSasService>>());
    }

    private static Dictionary<string, string> QueryOf(Uri uri)
        => uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty));

    [Test]
    public async Task AnAudioTarget_LandsWhereTheFunctionWillLookForIt()
    {
        var target = await _service.CreateAudioTargetAsync(JobId, ".wav");

        Assert.Multiple(() =>
        {
            Assert.That(target.BlobPath, Is.EqualTo(MediaProcessingStagingPaths.Source(JobId, ".wav")));
            Assert.That(target.BlobPath, Is.EqualTo("0f8fad5bd9cb469fa16570867728950e/source.wav"));
            Assert.That(target.SasUri.AbsolutePath, Does.Contain(StagingContainer));
        });
    }

    [Test]
    public async Task AnImageTarget_LandsInTheBatchFolderNotASongFolder()
    {
        // Which song an image belongs to is unknown until after matching, so it cannot go to a song
        // folder. The batch prefix also keeps DeleteStagedBlobsAsync, which sweeps by {guid}/, from
        // ever seeing one.
        var target = await _service.CreateMatchImageTargetAsync(JobId, 3, ".png");

        Assert.Multiple(() =>
        {
            Assert.That(target.BlobPath, Is.EqualTo(MediaProcessingStagingPaths.MatchBatchImage(JobId, 3, ".png")));
            Assert.That(target.BlobPath, Does.StartWith("batch/"));
        });
    }

    [Test]
    public async Task ATokenGrantsCreateAndWriteAndNothingElse()
    {
        // The single most important assertion here. No read means a leaked token cannot read back
        // even its own blob, let alone another creator's upload; no delete and no list mean it
        // cannot reach anything it did not write.
        var target = await _service.CreateAudioTargetAsync(JobId, ".mp3");
        var permissions = QueryOf(target.SasUri)["sp"];

        Assert.Multiple(() =>
        {
            Assert.That(permissions, Does.Contain("c"), "Create is needed for a single-PUT upload.");
            Assert.That(permissions, Does.Contain("w"), "Write is needed for Put Block / Put Block List.");
            Assert.That(permissions, Does.Not.Contain("r"), "Read would let a leaked token read blobs back.");
            Assert.That(permissions, Does.Not.Contain("d"), "Delete would let a leaked token destroy an upload.");
            Assert.That(permissions, Does.Not.Contain("l"), "List would expose other creators' staged files.");
        });
    }

    [Test]
    public async Task ATokenIsScopedToOneBlob()
    {
        // Container scope would grant write over every in-flight job folder and the whole batch
        // prefix in the environment. "sr=b" is what confines it to the one blob it was minted for.
        var target = await _service.CreateAudioTargetAsync(JobId, ".mp3");

        Assert.That(QueryOf(target.SasUri)["sr"], Is.EqualTo("b"));
    }

    [Test]
    public async Task ATokenStartsInThePastAndExpiresWithinItsConfiguredLifetime()
    {
        _options.StagingUploadSasLifetime = TimeSpan.FromMinutes(30);
        _options.StagingUploadSasClockSkew = TimeSpan.FromMinutes(5);

        var before = DateTimeOffset.UtcNow;
        var target = await _service.CreateAudioTargetAsync(JobId, ".mp3");
        var query = QueryOf(target.SasUri);

        var startsOn = DateTimeOffset.Parse(query["st"], System.Globalization.CultureInfo.InvariantCulture);
        var expiresOn = DateTimeOffset.Parse(query["se"], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            // Backdated deliberately: shared hosting's clock is not ours, and a host running a few
            // seconds fast would otherwise mint tokens Azure rejects on arrival.
            Assert.That(startsOn, Is.LessThan(before), "The token must already be valid when it is handed over.");
            Assert.That(expiresOn, Is.GreaterThan(before.AddMinutes(25)));
            Assert.That(expiresOn, Is.LessThanOrEqualTo(before.AddMinutes(31)));
            Assert.That(target.ExpiresOn, Is.EqualTo(expiresOn).Within(TimeSpan.FromSeconds(2)));
        });
    }

    [TestCase(".exe")]
    [TestCase(".txt")]
    [TestCase("")]
    [TestCase(null)]
    public void AnUnsupportedAudioExtension_IsRefused(string extension)
        => Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAudioTargetAsync(JobId, extension));

    [TestCase(".gif")]
    [TestCase(".svg")]
    [TestCase("")]
    public void AnUnsupportedImageExtension_IsRefused(string extension)
        => Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateMatchImageTargetAsync(JobId, 0, extension));

    // The server-side sibling of the token path, used for an image the browser cannot upload because
    // it is not in the FileList the direct uploader indexes into. It must be no more permissive about
    // what may be written where, which is what these three pin - all of them refuse before any
    // connection is attempted, so like everything else here they run entirely offline.

    [TestCase(".gif")]
    [TestCase(".svg")]
    [TestCase(".mp3")]
    [TestCase("")]
    public void StagingAnUnsupportedImageExtensionFromTheServer_IsRefused(string extension)
        => Assert.ThrowsAsync<InvalidDataException>(
            () => _service.StageMatchImageAsync(JobId, 0, extension, new MemoryStream([1, 2, 3])));

    [Test]
    public void StagingWithoutABatchOrASlot_IsRefused()
    {
        // The path is derived from these two and nothing else. A caller that could pass Guid.Empty or
        // a negative index would be naming a blob rather than being given one.
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _service.StageMatchImageAsync(Guid.Empty, 0, ".png", new MemoryStream([1])));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => _service.StageMatchImageAsync(JobId, -1, ".png", new MemoryStream([1])));
        });
    }

    [Test]
    public void StagingWithoutContent_IsACallerBugRatherThanAnEmptyBlob()
        => Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.StageMatchImageAsync(JobId, 0, ".png", null));

    [Test]
    public async Task AnExtensionIsNormalised_SoTheCasingACreatorTypedCannotReachABlobPath()
    {
        var target = await _service.CreateMatchImageTargetAsync(JobId, 0, "PNG");

        Assert.That(target.BlobPath, Does.EndWith("/0.png"));
    }

    [Test]
    public void AnEmptyGuid_IsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAudioTargetAsync(Guid.Empty, ".mp3"));
            Assert.ThrowsAsync<ArgumentException>(() => _service.CreateMatchImageTargetAsync(Guid.Empty, 0, ".png"));
        });
    }

    [Test]
    public void ANegativeIndex_IsRefused()
        => Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.CreateMatchImageTargetAsync(JobId, -1, ".png"));

    [Test]
    public void WithoutStagingConfigured_ItReportsUnavailableRatherThanThrowing()
    {
        // Matches how the rest of the pipeline degrades: an environment with no processing
        // configured still starts and still serves the catalogue, and the caller falls back to the
        // server-side upload path instead of failing the batch.
        var containers = new Mock<IBlobContainerFactory>();
        containers.Setup(factory => factory.GetUploadStagingContainer()).Returns((BlobContainerClient)null);

        var service = new UploadStagingSasService(
            containers.Object,
            Options.Create(new MediaProcessingOptions()),
            Mock.Of<ILogger<UploadStagingSasService>>());

        Assert.Multiple(() =>
        {
            Assert.That(service.IsAvailable, Is.False);
            Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAudioTargetAsync(JobId, ".mp3"));
        });
    }

    [Test]
    public async Task TwoTargetsForTheSameFileDifferOnlyByToken()
    {
        // Renewal must not move the destination - the browser may already have uploaded blocks there.
        var first = await _service.CreateAudioTargetAsync(JobId, ".mp3");
        var second = await _service.CreateAudioTargetAsync(JobId, ".mp3");

        Assert.Multiple(() =>
        {
            Assert.That(second.BlobPath, Is.EqualTo(first.BlobPath));
            Assert.That(second.SasUri.AbsolutePath, Is.EqualTo(first.SasUri.AbsolutePath));
        });
    }
}
