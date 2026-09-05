using System.IO;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

#nullable enable


/// <summary>
/// Who a set of playback URLs is being minted for.
///
/// <para>
/// Passed in rather than derived per song: a catalogue listing maps hundreds of rows, and resolving
/// the caller's subscription inside the mapper would put a lookup on every one of them. Optional so
/// the existing callers that only need metadata are unaffected — without it no HLS URL is produced,
/// which is the correct answer for a caller that has no listener in mind.
/// </para>
/// </summary>
/// <param name="UserId">The listener, or null when anonymous.</param>
/// <param name="HasFullAccess">
/// Whether they may hear whole songs. Baked into the token the URL carries, so entitlement is
/// decided once here rather than re-queried on every manifest request.
/// </param>
public sealed record MobileStreamContext(int? UserId, bool HasFullAccess);

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
    SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null, MobileStreamContext? streamContext = null);

    /// <param name="lyrics">See <see cref="MapToSongListItem"/>.</param>
    MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null, MobileStreamContext? streamContext = null);
}

public class MobileSongMapper : IMobileSongMapper
{
    private readonly IAzureStorageService _storageService;
    private readonly ICreatorPersonaService _creatorPersonaService;
    private readonly IHlsStreamUrlFactory _hlsUrlFactory;

    public MobileSongMapper(
        IAzureStorageService storageService,
        ICreatorPersonaService creatorPersonaService,
        IHlsStreamUrlFactory hlsUrlFactory)
    {
        _storageService = storageService;
        _creatorPersonaService = creatorPersonaService;
        _hlsUrlFactory = hlsUrlFactory;
    }

    public SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null, MobileStreamContext? streamContext = null)
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
            PersonaId = ResolvePersonaId(m),
            PersonaBio = ResolvePersonaBio(m),
            PersonaWebsiteUrl = ResolvePersonaWebsiteUrl(m),
            LyricsTimingsPath = ResolveLyricsTimingsPath(lyrics),
            LyricsVersion = ResolveLyricsVersion(lyrics),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            HlsUrl = ResolveHlsUrl(m, streamContext),
            AudioVersion = m.AudioContentVersion,
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

    public MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, StreamQualifyingSettings streamQualifying, SongLyrics? lyrics = null, MobileStreamContext? streamContext = null)
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
            PersonaId = ResolvePersonaId(m),
            PersonaBio = ResolvePersonaBio(m),
            PersonaWebsiteUrl = ResolvePersonaWebsiteUrl(m),
            LyricsTimingsPath = ResolveLyricsTimingsPath(lyrics),
            LyricsVersion = ResolveLyricsVersion(lyrics),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            HlsUrl = ResolveHlsUrl(m, streamContext),
            AudioVersion = m.AudioContentVersion,
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

    /// <summary>
    /// The persona id, but only when that persona is one a listener can actually act on.
    /// </summary>
    /// <remarks>
    /// A DISABLED persona is reported as null, deliberately and for the same reason
    /// <c>GetEffectiveArtistName</c> ignores it: the app must not offer a Follow button for an
    /// artist the server will then refuse to follow. Anywhere the name has fallen back past the
    /// persona, the id has to fall back with it.
    /// </remarks>
    private static int? ResolvePersonaId(SongMetadata m) =>
        m.Persona is { IsEnabled: true } ? m.PersonaId : null;

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


    /// <summary>
    /// The encrypted-HLS manifest URL for a song, or null when there is nothing to point at.
    ///
    /// <para>
    /// Null in two ordinary cases, neither of them an error: the caller supplied no listener, and
    /// the song has not been packaged yet. During the rollout the second is most of the catalogue,
    /// which is exactly why this is an additional field rather than a replacement for
    /// <c>StreamUrl</c>.
    /// </para>
    /// </summary>
    private string? ResolveHlsUrl(SongMetadata m, MobileStreamContext? streamContext)
        => streamContext is null
            ? null
            : _hlsUrlFactory.BuildManifestUrl(m, streamContext.UserId, streamContext.HasFullAccess);
}
