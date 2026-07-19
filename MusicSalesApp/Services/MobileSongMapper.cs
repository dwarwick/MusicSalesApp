using System.IO;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

#nullable enable

/// <summary>
/// Shared mapping logic from <see cref="SongMetadata"/> to DTOs used by the
/// mobile controllers (songs list and playlist songs).
/// </summary>
public interface IMobileSongMapper
{
    SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, int defaultStreamQualifyingSeconds);
    MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, int defaultStreamQualifyingSeconds);
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

    public SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime, int defaultStreamQualifyingSeconds)
    {
        return new SongListItemDto
        {
            Id = m.Id,
            SongTitle = ResolveTitle(m),
            ArtistName = m.GetEffectiveArtistName(),
            Genre = m.Genre ?? string.Empty,
            AlbumArtUrl = ResolveAlbumArtUrl(m, sasLifetime),
            PersonaImageUrl = ResolvePersonaImageUrl(m, sasLifetime),
            PersonaBio = ResolvePersonaBio(m),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            StreamCount = m.NumberOfStreams,
            StreamQualifyingSeconds = ResolveStreamQualifyingSeconds(m, defaultStreamQualifyingSeconds),
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

    public MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId, int defaultStreamQualifyingSeconds)
    {
        return new MobilePlaylistSongDto
        {
            UserPlaylistId = userPlaylistId,
            SongMetadataId = m.Id,
            SongTitle = ResolveTitle(m),
            ArtistName = m.GetEffectiveArtistName(),
            Genre = m.Genre ?? string.Empty,
            AlbumArtUrl = ResolveAlbumArtUrl(m, sasLifetime),
            PersonaImageUrl = ResolvePersonaImageUrl(m, sasLifetime),
            PersonaBio = ResolvePersonaBio(m),
            StreamUrl = _storageService.GetReadSasUri(m.Mp3BlobPath!, sasLifetime).ToString(),
            StreamCount = m.NumberOfStreams,
            StreamQualifyingSeconds = ResolveStreamQualifyingSeconds(m, defaultStreamQualifyingSeconds),
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

    private static string? ResolvePersonaBio(SongMetadata m) =>
        m.Persona is { IsEnabled: true, Bio: not null and not "" }
            ? m.Persona.Bio
            : m.Creator?.Bio;

    private static int ResolveStreamQualifyingSeconds(SongMetadata m, int defaultStreamQualifyingSeconds) =>
        m.Creator?.StreamQualifyingSeconds ?? defaultStreamQualifyingSeconds;
}
