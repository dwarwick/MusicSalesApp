using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ArtistFollowServiceTests
{
    private ArtistFollowTestHarness _harness;
    private ArtistFollowService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();
        _service = new ArtistFollowService(
            _harness.ContextFactory.Object,
            new ArtistFollowerIdentityService(new Random(20260904)),
            Mock.Of<ILogger<ArtistFollowService>>());
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task SetFollowState_CreatesTheRelationship()
    {
        var outcome = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using var context = _harness.NewContext();
        var follow = await context.ArtistFollowers.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.Followed));
            Assert.That(follow.IsActive, Is.True);
            Assert.That(follow.ReleaseNotificationsEnabled, Is.True);
            Assert.That(follow.ArtistMessagesEnabled, Is.True);
            Assert.That(follow.AnonymousListenerNumber, Is.GreaterThanOrEqualTo(1000));
        });
    }

    [Test]
    public async Task SetFollowState_IsIdempotent()
    {
        // The mobile client replays queued intents after a reconnect, so following twice must be a
        // no-op rather than a second row or a toggle back off.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var second = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(second, Is.EqualTo(ArtistFollowOutcome.AlreadyFollowing));
            Assert.That(await context.ArtistFollowers.CountAsync(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SetFollowState_UnfollowIsASoftDeleteThatKeepsThePseudonym()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        int originalNumber;
        await using (var context = _harness.NewContext())
        {
            originalNumber = (await context.ArtistFollowers.SingleAsync()).AnonymousListenerNumber;
        }

        var unfollowed = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);
        var refollowed = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using var verify = _harness.NewContext();
        var follow = await verify.ArtistFollowers.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(unfollowed, Is.EqualTo(ArtistFollowOutcome.Unfollowed));
            Assert.That(refollowed, Is.EqualTo(ArtistFollowOutcome.Followed));
            Assert.That(follow.IsActive, Is.True);
            Assert.That(follow.UnfollowedDateUtc, Is.Null);

            // The point of the soft delete: to the creator this is the same listener returning.
            Assert.That(follow.AnonymousListenerNumber, Is.EqualTo(originalNumber));
        });
    }

    [Test]
    public async Task SetFollowState_UnfollowingWhenNotFollowingChangesNothing()
    {
        var outcome = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.NotFollowing));
    }

    [Test]
    public async Task SetFollowState_RecordsTheSongThatPromptedTheFollow()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true, _harness.SongId);

        await using var context = _harness.NewContext();
        var follow = await context.ArtistFollowers.SingleAsync();

        Assert.That(follow.SourceSongMetadataId, Is.EqualTo(_harness.SongId));
    }

    [Test]
    public async Task SetFollowState_IgnoresASourceSongBelongingToSomeoneElse()
    {
        // Otherwise a caller could credit any song at all, and the creator's "Followed After
        // Listening To" column would report follows that song never earned.
        int foreignSongId;
        await using (var seed = _harness.NewContext())
        {
            var song = new SongMetadata
            {
                SongTitle = "Not Mine",
                Mp3BlobPath = "songs/not-mine.mp3",
                BlobPath = "songs/not-mine.mp3",
                IsActive = true,
                IsEnabled = true,
            };
            seed.SongMetadata.Add(song);
            await seed.SaveChangesAsync();
            foreignSongId = song.Id;
        }

        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true, foreignSongId);

        await using var context = _harness.NewContext();
        var follow = await context.ArtistFollowers.SingleAsync();

        Assert.That(follow.SourceSongMetadataId, Is.Null);
    }

    [Test]
    public async Task SetFollowState_RefusesToLetSomeoneFollowTheirOwnPersona()
    {
        // The UI leaves the bell off your own songs, but the mobile API takes a persona id from the
        // client, so the refusal has to live here. Following yourself would put a creator in their
        // own follower list, follower count and "new this month" figure.
        var outcome = await _service.SetFollowStateAsync(
            _harness.PersonaId, _harness.CreatorUserId, true);

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.CannotFollowSelf));
            Assert.That(await context.ArtistFollowers.AnyAsync(), Is.False);
        });
    }

    [Test]
    public async Task SetFollowState_SelfFollowIsRefusedDistinctlyFromAnUnavailableArtist()
    {
        // Separate outcomes because they mean different things to a client: one is "that is you",
        // the other is "that artist is gone". Both are permanent, so both answer 400, but the
        // message a listener sees should not be wrong.
        var self = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.CreatorUserId, true);
        var missing = await _service.SetFollowStateAsync(999999, _harness.ListenerUserId, true);

        Assert.Multiple(() =>
        {
            Assert.That(self, Is.EqualTo(ArtistFollowOutcome.CannotFollowSelf));
            Assert.That(missing, Is.EqualTo(ArtistFollowOutcome.ArtistUnavailable));
        });
    }

    [Test]
    public async Task SetFollowState_RefusesADisabledPersona()
    {
        await using (var context = _harness.NewContext())
        {
            var persona = await context.CreatorPersonas.SingleAsync(p => p.Id == _harness.PersonaId);
            persona.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var outcome = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.ArtistUnavailable));
    }

    [Test]
    public async Task SetFollowState_RefusesASuspendedCreator()
    {
        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.CreatorUserId);
            user.IsSuspended = true;
            await context.SaveChangesAsync();
        }

        var outcome = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.ArtistUnavailable));
    }

    [Test]
    public async Task SetFollowState_UnfollowingASuspendedArtistStillWorks()
    {
        // A listener must be able to walk away from an artist who has since been suspended;
        // applying the availability check to unfollow would strand them.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.CreatorUserId);
            user.IsSuspended = true;
            await context.SaveChangesAsync();
        }

        var outcome = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        Assert.That(outcome, Is.EqualTo(ArtistFollowOutcome.Unfollowed));
    }

    [Test]
    public async Task SetBlocked_EndsTheFollowAndRefusesToFollowAgain()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var blocked = await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var refollow = await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using var context = _harness.NewContext();
        var follow = await context.ArtistFollowers.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(blocked, Is.True);
            Assert.That(follow.IsBlockedByListener, Is.True);
            Assert.That(follow.IsActive, Is.False, "Blocking implies unfollowing.");
            Assert.That(refollow, Is.EqualTo(ArtistFollowOutcome.Blocked));
        });
    }

    [Test]
    public async Task GetFollowedArtists_KeepsABlockedArtistSoTheBlockCanBeUndone()
    {
        // This list is the only place a listener can unblock on the web, and blocking sets
        // IsActive to false - so a filter on IsActive alone drops the row, taking the Unblock
        // button with it and stranding the block permanently. It shipped that way once.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var listed = await _service.GetFollowedArtistsAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(listed, Has.Count.EqualTo(1));
            Assert.That(listed[0].IsBlocked, Is.True, "and it has to render AS blocked, not as a follow");
        });
    }

    [Test]
    public async Task GetFollowedArtists_DropsAPlainUnfollowWhichHasNothingLeftToUndo()
    {
        // The counterpart to the test above, and the reason the filter is not simply removed:
        // unfollowing without blocking leaves nothing to act on, so the row must not linger.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        Assert.That(await _service.GetFollowedArtistsAsync(_harness.ListenerUserId), Is.Empty);
    }

    [Test]
    public async Task SetBlocked_DoesNotStopTheArtistFollowingTheListenerBack()
    {
        // Blocking is one-directional by design: it governs what reaches the listener, not what
        // the creator may do elsewhere. Pinning it down because the reverse is easy to assume.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var listenerPersonaId = await _harness.AddPersonaForListenerAsync("Blocked Listener");

        var followBack = await _service.SetFollowStateAsync(
            listenerPersonaId, _harness.CreatorUserId, true);

        Assert.That(followBack, Is.EqualTo(ArtistFollowOutcome.Followed));
    }

    [Test]
    public async Task SetBlocked_UnblockingDoesNotSilentlyRestoreTheFollow()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        Assert.That(
            await _service.IsFollowingAsync(_harness.PersonaId, _harness.ListenerUserId),
            Is.False,
            "Unblocking is not consent to follow again.");
    }

    [Test]
    public async Task IsFollowing_IsFalseForABlockedRelationship()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        Assert.That(await _service.IsFollowingAsync(_harness.PersonaId, _harness.ListenerUserId), Is.False);
    }

    [Test]
    public async Task GetFollowerCounts_ReportsZeroForAPersonaWithNoFollowers()
    {
        // Present-with-zero rather than absent, so a caller never has to tell missing from empty.
        var counts = await _service.GetFollowerCountsAsync([_harness.PersonaId, 9999]);

        Assert.Multiple(() =>
        {
            Assert.That(counts[_harness.PersonaId], Is.Zero);
            Assert.That(counts[9999], Is.Zero);
        });
    }

    [Test]
    public async Task GetFollowerCounts_CountsOnlyLiveFollows()
    {
        var second = _harness.AddListener("second@test.com");

        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetFollowStateAsync(_harness.PersonaId, second, true);
        await _service.SetFollowStateAsync(_harness.PersonaId, second, false);

        var counts = await _service.GetFollowerCountsAsync([_harness.PersonaId]);

        Assert.That(counts[_harness.PersonaId], Is.EqualTo(1));
    }

    [Test]
    public async Task GetFollowedArtists_ShowsTheLatestReleaseAndUnreadCount()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ocean Road", DateTime.UtcNow.AddDays(-1));

        var followed = await _service.GetFollowedArtistsAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(followed, Has.Count.EqualTo(1));
            Assert.That(followed[0].ArtistName, Is.EqualTo("Alex Rivers"));
            Assert.That(followed[0].LatestReleaseTitle, Is.EqualTo("Ocean Road"));
            Assert.That(followed[0].UnreadMessageCount, Is.Zero);
            Assert.That(followed[0].IsBlocked, Is.False);
        });
    }

    [Test]
    public async Task GetFollowedArtists_ExcludesArtistsTheListenerHasUnfollowed()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        Assert.That(await _service.GetFollowedArtistsAsync(_harness.ListenerUserId), Is.Empty);
    }

    [Test]
    public async Task SetArtistNotificationPreferences_MutesWithoutUnfollowing()
    {
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var updated = await _service.SetArtistNotificationPreferencesAsync(
            _harness.PersonaId, _harness.ListenerUserId, releaseNotificationsEnabled: false, artistMessagesEnabled: null);

        await using var context = _harness.NewContext();
        var follow = await context.ArtistFollowers.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.True);
            Assert.That(follow.ReleaseNotificationsEnabled, Is.False);
            Assert.That(follow.ArtistMessagesEnabled, Is.True, "A null argument must leave the other preference alone.");
            Assert.That(follow.IsActive, Is.True, "Muting is not unfollowing.");
        });
    }

    [Test]
    public async Task TheDatabaseRefusesASecondFollowRowForTheSamePair()
    {
        // The service checks for an existing row first, so this goes around it to prove the unique
        // index is what actually makes a duplicate impossible - the guarantee two concurrent
        // requests depend on.
        await _service.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using var context = _harness.NewContext();
        context.ArtistFollowers.Add(new ArtistFollower
        {
            CreatorPersonaId = _harness.PersonaId,
            ListenerUserId = _harness.ListenerUserId,
            FollowedDateUtc = DateTime.UtcNow,
            IsActive = true,
            AnonymousListenerNumber = 4817,
        });

        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }
}
