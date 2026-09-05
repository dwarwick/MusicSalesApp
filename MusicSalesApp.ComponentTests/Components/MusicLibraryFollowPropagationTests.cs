using MusicSalesApp.Components.Pages.Public;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// Following is artist-level while a card is song-level, so one library page routinely shows a
/// dozen Follow buttons for the same artist. They have to agree.
/// </summary>
/// <remarks>
/// Exercised through a subclass rather than a full render: the whole mechanism is the shared
/// followed-persona set that every card reads on each render, and rendering a library needs storage,
/// songs and JS interop to say nothing more than these four asserts do.
/// </remarks>
[TestFixture]
public class MusicLibraryFollowPropagationTests
{
    private sealed class TestableMusicLibrary : MusicLibraryModel
    {
        public bool? Following(int? personaId) => IsFollowingArtist(personaId);

        public void CardChanged(int? personaId, bool isFollowing) =>
            OnArtistFollowStateChanged(personaId, isFollowing);
    }

    [Test]
    public void FollowingFromOneCard_MakesEveryOtherCardByThatArtistAgree()
    {
        var page = new TestableMusicLibrary();

        page.CardChanged(30, true);

        // Every card passes its own persona id, so one entry answers for all of them.
        Assert.That(page.Following(30), Is.True);
    }

    [Test]
    public void UnfollowingFromOneCard_ClearsTheOthersToo()
    {
        var page = new TestableMusicLibrary();
        page.CardChanged(30, true);

        page.CardChanged(30, false);

        Assert.That(page.Following(30), Is.False);
    }

    [Test]
    public void FollowingOneArtist_LeavesOtherArtistsAlone()
    {
        var page = new TestableMusicLibrary();

        page.CardChanged(30, true);

        Assert.Multiple(() =>
        {
            Assert.That(page.Following(30), Is.True);
            Assert.That(page.Following(31), Is.False);
        });
    }

    [Test]
    public void ASongWithNoPersonaIsIgnoredRatherThanTracked()
    {
        // A song whose artist came from free text has no artist entity, so its card renders no
        // button at all - and null must stay null rather than becoming a followed entry.
        var page = new TestableMusicLibrary();

        page.CardChanged(null, true);

        Assert.That(page.Following(null), Is.Null);
    }

    [Test]
    public void RepeatingTheSameChangeIsHarmless()
    {
        // Two cards for one artist can both report the same new state - the clicked one and a
        // sibling re-raising on re-render - and the set must not care.
        var page = new TestableMusicLibrary();

        page.CardChanged(30, true);
        page.CardChanged(30, true);
        page.CardChanged(30, false);
        page.CardChanged(30, false);

        Assert.That(page.Following(30), Is.False);
    }
}
