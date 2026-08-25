using System.IO;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

#nullable enable

/// <summary>
/// Shared mapping logic from <see cref="SongMetadata"/> to DTOs used by the
/// mobile controllers (songs list and playlist songs).
/// </summary>
public interface IMobileSongMapper
{
    /// <param name="lyrics">
    /// This song's lyrics row, when the caller has loaded one. Optional because most callers do
    /// not need it; pass it and the response carries a fetchable timings path.
    /// </param>
    SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null);

    /// <param name="lyrics">See <see cref="MapToSongListItem"/>.</param>
    MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null);
}

public class MobileSongMapper : IMobileSongMapper
{
    private readonly IAzureStorageService _storageService;
    private readonly ICreatorPersonaService _creatorPersonaService;

    public MobileSongMapper(
        IAzureStorageService storageService,
        ICreatorPersonaService creatorPersonaService)
    {
        _storageService = storageService;
        _creatorPersonaService = creatorPersonaService;
    }

    public SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null)
    {
        return new SongListItemDto
        {
            Id = m.Id,
            SongTitle = ResolveTitle(m),
            ArtistName = m.GetEffectiveArtistName(),
            Genre = m.Genre ?? string.Empty,
            AlbumArtUrl = ResolveAlbumArtUrl(m, sasLifetime),
            AlbumArtThumbUrl = ResolveAlbumArtVariantUrl(m, sasLifetime, ImageVariantSizes.MobileThumbWidth),
            AlbumArtHeroUrl = ResolveAlbumArtVariantUrl(m, sasLifetime, ImageVariantSizes.MobileHeroWidth),
            AlbumArtVersion = m.CoverArtVariantVersion,
            PersonaImageUrl = ResolvePersonaImageUrl(m, sasLifetime),
            PersonaImageThumbUrl = ResolvePersonaImageVariantUrl(m, sasLifetime, ImageVariantSizes.MobileThumbWidth),
            PersonaImageHeroUrl = ResolvePersonaImageVariantUrl(m, sasLifetime, ImageVariantSizes.MobileHeroWidth),
            PersonaImageVersion = m.Persona?.ImageVariantVersion ?? 0,
            PersonaBio = ResolvePersonaBio(m),
            PersonaWebsiteUrl = ResolvePersonaWebsiteUrl(m),
            LyricsTimingsPath = ResolveLyricsTimingsPath(lyrics),
            LyricsVersion = ResolveLyricsVersion(lyrics),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            StreamCount = m.NumberOfStreams,
            StreamQualifyingSeconds = streamQualifying.Resolve(m.Creator?.StreamQualifyingSeconds),
            TrackLengthSeconds = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            DisplayOrder = m.DisplayOrder,
            IsAiGenerated = m.IsAiGenerated,
            IsAiVocals = m.IsAiVocals,
            IsAiLyrics = m.IsAiLyrics,
            CreatorId = m.CreatorId,
            CreatorUserId = m.Creator?.UserId
        };
    }

    public MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null)
    {
        return new MobilePlaylistSongDto
        {
            UserPlaylistId = userPlaylistId,
            SongMetadataId = m.Id,
            SongTitle = ResolveTitle(m),
            ArtistName = m.GetEffectiveArtistName(),
            Genre = m.Genre ?? string.Empty,
            AlbumArtUrl = ResolveAlbumArtUrl(m, sasLifetime),
            AlbumArtThumbUrl = ResolveAlbumArtVariantUrl(m, sasLifetime, ImageVariantSizes.MobileThumbWidth),
            AlbumArtHeroUrl = ResolveAlbumArtVariantUrl(m, sasLifetime, ImageVariantSizes.MobileHeroWidth),
            AlbumArtVersion = m.CoverArtVariantVersion,
            PersonaImageUrl = ResolvePersonaImageUrl(m, sasLifetime),
            PersonaImageThumbUrl = ResolvePersonaImageVariantUrl(m, sasLifetime, ImageVariantSizes.MobileThumbWidth),
            PersonaImageHeroUrl = ResolvePersonaImageVariantUrl(m, sasLifetime, ImageVariantSizes.MobileHeroWidth),
            PersonaImageVersion = m.Persona?.ImageVariantVersion ?? 0,
            PersonaBio = ResolvePersonaBio(m),
            PersonaWebsiteUrl = ResolvePersonaWebsiteUrl(m),
            LyricsTimingsPath = ResolveLyricsTimingsPath(lyrics),
            LyricsVersion = ResolveLyricsVersion(lyrics),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            StreamCount = m.NumberOfStreams,
            StreamQualifyingSeconds = streamQualifying.Resolve(m.Creator?.StreamQualifyingSeconds),
            TrackLengthSeconds = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            DisplayOrder = m.DisplayOrder,
            IsAiGenerated = m.IsAiGenerated,
            IsAiVocals = m.IsAiVocals,
            IsAiLyrics = m.IsAiLyrics,
            CreatorId = m.CreatorId,
            CreatorUserId = m.Creator?.UserId
        };
    }

    private static string ResolveTitle(SongMetadata m) =>
        Common.Helpers.SongTitleHelper.GetEffectiveTitle(m.SongTitle, m.Mp3BlobPath, m.BlobPath);

    private string? ResolveAlbumArtUrl(SongMetadata m, TimeSpan sasLifetime) =>
        !string.IsNullOrEmpty(m.ImageBlobPath)
            ? _storageService.GetReadSasUri(m.ImageBlobPath, sasLifetime).ToString()
            : null;

    private string? ResolvePersonaImageUrl(SongMetadata m, TimeSpan sasLifetime) =>
        m.Persona is { IsEnabled: true, ImageBlobPath: not null and not "" }
            ? _creatorPersonaService.GetPersonaImageSasUrl(m.Persona.ImageBlobPath, sasLifetime)
            : null;

    /// <summary>
    /// A rendition URL, or null when that rendition does not exist for this song.
    ///
    /// <para>
    /// Null rather than a guess: the app's fallback chain ends at
    /// <see cref="SongListItemDto.AlbumArtUrl"/>, so a missing rendition degrades to today's
    /// behaviour, whereas a URL pointing at a blob that was never generated would be a broken image.
    /// </para>
    /// </summary>
    private string? ResolveAlbumArtVariantUrl(SongMetadata m, TimeSpan sasLifetime, int width) =>
        !string.IsNullOrEmpty(m.ImageBlobPath)
        && ImageVariantSizes.CsvContains(m.CoverArtVariantWidths, width)
            ? _storageService
                .GetReadSasUri(ImageVariantPaths.Variant(m.ImageBlobPath, width), sasLifetime)
                .ToString()
            : null;

    private string? ResolvePersonaImageVariantUrl(SongMetadata m, TimeSpan sasLifetime, int width) =>
        m.Persona is { IsEnabled: true, ImageBlobPath: not null and not "" }
        && ImageVariantSizes.CsvContains(m.Persona.ImageVariantWidths, width)
            ? _creatorPersonaService.GetPersonaImageSasUrl(
                ImageVariantPaths.Variant(m.Persona.ImageBlobPath, width), sasLifetime)
            : null;

    private static string? ResolvePersonaBio(SongMetadata m) =>
        m.Persona is { IsEnabled: true, Bio: not null and not "" }
            ? m.Persona.Bio
            : m.Creator?.Bio;

    /// <summary>
    /// The persona's website, or null. No creator fallback - a Creator has no website column.
    /// </summary>
    private static string? ResolvePersonaWebsiteUrl(SongMetadata m) =>
        m.Persona is { IsEnabled: true, WebsiteUrl: not null and not "" }
            ? m.Persona.WebsiteUrl
            : null;

    /// <summary>
    /// The timings path a listener may fetch, or null.
    /// </summary>
    /// <remarks>
    /// Gated on the ROW rather than on a path existing, for the same reason
    /// <c>IsPubliclyReadableAsync</c> is: withheld timings sit at the identical blob path, so
    /// "there is a file" proves nothing. Since alignment stopped publishing, NeedsReview is where
    /// every successful run lands - it is the common case here, not an edge one.
    ///
    /// <para>
    /// Null here is what takes lyrics off a phone. The app asks for nothing it was not given a path
    /// for, so a hidden or taken-down song simply arrives without one and the player shows cover art
    /// - it needs no notion of "disabled" at all, and no release to learn one.
    /// </para>
    /// </remarks>
    private static string? ResolveLyricsTimingsPath(SongLyrics? lyrics) =>
        lyrics is { IsVisibleToListeners: true }
        && !string.IsNullOrWhiteSpace(lyrics.TimingsBlobPath)
            ? lyrics.TimingsBlobPath
            : null;

    /// <summary>Zero whenever there is no readable path, so the two always travel together.</summary>
    private static int ResolveLyricsVersion(SongLyrics? lyrics) =>
        ResolveLyricsTimingsPath(lyrics) is null ? 0 : lyrics!.Version;

}
