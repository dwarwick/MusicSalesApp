using System.Reflection;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Components.Players;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class WebPlaybackRestrictionTests
{
    [Test]
    public void MusicLibrary_FeaturedCardTrack_IsUnrestrictedForAnonymousUser()
    {
        var model = new TestMusicLibraryModel();
        SetField(model, "_playingFileName", "songs/featured.mp3");
        SetField(model, "_homePageSongs", new HashSet<string> { "songs/featured.mp3" });

        Assert.That(model.CallIsCurrentPlayingTrackRestricted(), Is.False);
    }

    [Test]
    public void MusicLibrary_NonFeaturedCardTrack_IsRestrictedForAnonymousUser()
    {
        var model = new TestMusicLibraryModel();
        SetField(model, "_playingFileName", "songs/standard.mp3");
        SetField(model, "_homePageSongs", new HashSet<string>());

        Assert.That(model.CallIsCurrentPlayingTrackRestricted(), Is.True);
    }

    [Test]
    public void PlaylistPlayer_FeaturedTrack_IsUnrestrictedForAnonymousUser()
    {
        var model = new TestPlaylistPlayerInteractiveModel();
        SetPlaylistTrack(model, "songs/featured.mp3");
        SetField(model, "_metadataLookup", new Dictionary<string, SongMetadata>
        {
            ["songs/featured.mp3"] = new SongMetadata { DisplayOnHomePage = true }
        });

        Assert.That(model.CallIsTrackRestricted(0), Is.False);
    }

    [Test]
    public void PlaylistPlayer_NonFeaturedTrack_IsRestrictedForAnonymousUser()
    {
        var model = new TestPlaylistPlayerInteractiveModel();
        SetPlaylistTrack(model, "songs/standard.mp3");
        SetField(model, "_metadataLookup", new Dictionary<string, SongMetadata>
        {
            ["songs/standard.mp3"] = new SongMetadata()
        });

        Assert.That(model.CallIsTrackRestricted(0), Is.True);
    }

    [Test]
    public void SongPlayer_FeaturedTrack_IsUnrestrictedForAnonymousUser()
    {
        var model = new TestSongPlayerInteractiveModel();
        SetField(model, "_songMetadata", new SongMetadata { DisplayOnHomePage = true });

        Assert.That(model.CallIsProgressBarRestricted(), Is.False);
    }

    [Test]
    public void SongPlayer_NonFeaturedTrack_IsRestrictedForAnonymousUser()
    {
        var model = new TestSongPlayerInteractiveModel();
        SetField(model, "_songMetadata", new SongMetadata());

        Assert.That(model.CallIsProgressBarRestricted(), Is.True);
    }

    private static void SetPlaylistTrack(TestPlaylistPlayerInteractiveModel model, string fileName)
    {
        SetField(model, "_playlistInfo", new PlaylistInfo
        {
            Tracks = new List<StorageFileInfo>
            {
                new() { Name = fileName }
            }
        });
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var type = target.GetType();
        while (type != null)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            type = type.BaseType;
        }

        Assert.Fail($"Field '{fieldName}' was not found on {target.GetType().Name}.");
    }

    private sealed class TestMusicLibraryModel : MusicLibraryModel
    {
        public bool CallIsCurrentPlayingTrackRestricted()
        {
            return IsCurrentPlayingTrackRestricted();
        }
    }

    private sealed class TestPlaylistPlayerInteractiveModel : PlaylistPlayerInteractiveModel
    {
        public bool CallIsTrackRestricted(int trackIndex)
        {
            return IsTrackRestricted(trackIndex);
        }
    }

    private sealed class TestSongPlayerInteractiveModel : SongPlayerInteractiveModel
    {
        public bool CallIsProgressBarRestricted()
        {
            return IsProgressBarRestricted();
        }
    }
}
