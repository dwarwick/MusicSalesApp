#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// This service is half of a GET/PUT pair over one record: the app reads all five preferences, the
/// listener toggles one, and the app writes all five back. So the read has to be lossless, or the
/// write destroys whatever the read misreported.
/// </summary>
[TestFixture]
public class ArtistNotificationPreferenceServiceTests
{
    private ArtistFollowTestHarness _harness;
    private ArtistNotificationPreferenceService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();
        _service = new ArtistNotificationPreferenceService(_harness.ContextFactory.Object);
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private async Task SetEverythingOnAsync()
    {
        await _service.SetAsync(_harness.ListenerUserId, new ArtistNotificationPreferences
        {
            ReceiveArtistReleaseEmails = true,
            ReceiveArtistMessageEmails = true,
            ReceiveArtistReleasePush = true,
            ReceiveArtistMessagePush = true,
            ArtistPushFrequency = ArtistPushFrequency.Daily,
        });
    }

    private async Task SuspendAsync()
    {
        await using var context = _harness.NewContext();
        var user = await context.Users.SingleAsync(row => row.Id == _harness.ListenerUserId);
        user.IsSuspended = true;
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task EveryPreferenceRoundTrips()
    {
        await SetEverythingOnAsync();

        var read = await _service.GetAsync(_harness.ListenerUserId);

        Assert.That(read, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(read!.ReceiveArtistReleaseEmails, Is.True);
            Assert.That(read.ReceiveArtistMessageEmails, Is.True);
            Assert.That(read.ReceiveArtistReleasePush, Is.True);
            Assert.That(read.ReceiveArtistMessagePush, Is.True);
            Assert.That(read.ArtistPushFrequency, Is.EqualTo(ArtistPushFrequency.Daily));
        });
    }

    [Test]
    public async Task ASuspendedAccountIsShownWhatIsActuallyStored()
    {
        // These four used to be masked to false while suspended, on the reasoning that reporting
        // them promises mail nobody will receive. The reasoning was sound and the mask was still
        // wrong - see the round trip below, which is the reason.
        await SetEverythingOnAsync();
        await SuspendAsync();

        var read = await _service.GetAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(read!.ReceiveArtistReleaseEmails, Is.True);
            Assert.That(read.ReceiveArtistMessageEmails, Is.True);
            Assert.That(read.ReceiveArtistReleasePush, Is.True);
            Assert.That(read.ReceiveArtistMessagePush, Is.True);
        });
    }

    [Test]
    public async Task ASuspendedAccountsReadModifyWriteDoesNotEraseTheOtherPreferences()
    {
        // The bug, exactly as it happened: suspended listener opens Config > Notifications, the app
        // GETs all five, they toggle ONE, the app PUTs all five back. With the read masked, the
        // four they never touched came back false and were persisted that way - so lifting the
        // suspension restored an account opted out of everything, with no record it ever was not.
        await SetEverythingOnAsync();
        await SuspendAsync();

        var read = await _service.GetAsync(_harness.ListenerUserId);
        read!.ReceiveArtistMessageEmails = false;
        await _service.SetAsync(_harness.ListenerUserId, read);

        await using var context = _harness.NewContext();
        var user = await context.Users.SingleAsync(row => row.Id == _harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(user.ReceiveArtistMessageEmails, Is.False, "The one they actually changed.");
            Assert.That(user.ReceiveArtistReleaseEmails, Is.True);
            Assert.That(user.ReceiveArtistReleasePush, Is.True);
            Assert.That(user.ReceiveArtistMessagePush, Is.True);
            Assert.That(user.ArtistPushFrequency, Is.EqualTo((int)ArtistPushFrequency.Daily));
        });
    }

    [Test]
    public async Task WritingRotatesTheConcurrencyStamp()
    {
        // This writes AspNetUsers outside Identity, which rotates the stamp for itself. Without
        // this, a /manage-account page opened before the phone's write still carried a matching
        // stamp, so its UserManager.UpdateAsync - which writes EVERY mapped column, including the
        // two push preferences that page has no control for - passed the concurrency check and
        // silently reverted whatever had just been set from the phone.
        string before;

        await using (var context = _harness.NewContext())
        {
            before = (await context.Users.SingleAsync(row => row.Id == _harness.ListenerUserId)).ConcurrencyStamp!;
        }

        await SetEverythingOnAsync();

        await using var after = _harness.NewContext();
        var stamp = (await after.Users.SingleAsync(row => row.Id == _harness.ListenerUserId)).ConcurrencyStamp;

        Assert.That(stamp, Is.Not.EqualTo(before));
    }

    [Test]
    public async Task AnOutOfRangeFrequencyIsNormalisedOnTheWayIn()
    {
        // The column is an int. An undefined value would otherwise make the dispatcher's window
        // lookup fall back to Instant on every run rather than once, here.
        await _service.SetAsync(_harness.ListenerUserId, new ArtistNotificationPreferences
        {
            ArtistPushFrequency = (ArtistPushFrequency)99,
        });

        var read = await _service.GetAsync(_harness.ListenerUserId);

        Assert.That(read!.ArtistPushFrequency, Is.EqualTo(ArtistPushFrequency.Instant));
    }

    [Test]
    public async Task SavingEmailPreferencesLeavesThePhonePreferencesAlone()
    {
        // The reported failure, in order: the listener sets release push from the phone, then ticks
        // an email box on a /manage-account page that has been open since before that. The web save
        // must write its own three columns and nothing else.
        //
        // It used to go through UserManager.UpdateAsync, which writes EVERY mapped column back from
        // the page's snapshot, so the push preferences were silently reverted. Re-reading first did
        // not help - the page's user is already tracked, so the query returns that same stale
        // instance - and the write then failed with "Optimistic concurrency failure, object has
        // been modified" instead.
        await SetEverythingOnAsync();

        var saved = await _service.SetEmailPreferencesAsync(
            _harness.ListenerUserId,
            receiveNewSongEmails: true,
            receiveArtistReleaseEmails: false,
            receiveArtistMessageEmails: true);

        await using var context = _harness.NewContext();
        var user = await context.Users.SingleAsync(row => row.Id == _harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(saved, Is.True);

            Assert.That(user.ReceiveNewSongEmails, Is.True);
            Assert.That(user.ReceiveArtistReleaseEmails, Is.False, "The one that changed.");
            Assert.That(user.ReceiveArtistMessageEmails, Is.True);

            Assert.That(user.ReceiveArtistReleasePush, Is.True, "Set from the phone; the web must not touch it.");
            Assert.That(user.ReceiveArtistMessagePush, Is.True);
            Assert.That(user.ArtistPushFrequency, Is.EqualTo((int)ArtistPushFrequency.Daily));
        });
    }

    [Test]
    public async Task SavingEmailPreferencesDoesNotRotateTheConcurrencyStamp()
    {
        // The opposite of SetAsync above, and deliberately so. Rotating announces "the row you are
        // holding is out of date", which is only worth saying when the writer could have clobbered
        // something. This one writes three named columns and can clobber nothing, so rotating would
        // do nothing but break whatever other page happens to be open.
        string before;

        await using (var context = _harness.NewContext())
        {
            before = (await context.Users.SingleAsync(row => row.Id == _harness.ListenerUserId)).ConcurrencyStamp!;
        }

        await _service.SetEmailPreferencesAsync(_harness.ListenerUserId, true, true, true);

        await using var after = _harness.NewContext();
        var stamp = (await after.Users.SingleAsync(row => row.Id == _harness.ListenerUserId)).ConcurrencyStamp;

        Assert.That(stamp, Is.EqualTo(before));
    }

    [Test]
    public async Task SavingEmailPreferencesReportsFailureForAnUnknownUser()
    {
        Assert.That(await _service.SetEmailPreferencesAsync(-1, true, true, true), Is.False);
    }

    [Test]
    public async Task NothingIsWrittenForAnUnknownUser()
    {
        Assert.That(await _service.SetAsync(-1, new ArtistNotificationPreferences()), Is.False);
    }

    [Test]
    public async Task NothingIsReadForAnUnknownUser()
    {
        Assert.That(await _service.GetAsync(-1), Is.Null);
    }
}
