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
    SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime);
    MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId);
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

    public SongListItemDto MapToSongListItem(SongMetadata m, TimeSpan sasLifetime)
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
            TrackLengthSeconds = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            CreatorId = m.CreatorId,
            CreatorUserId = m.Creator?.UserId
        };
    }

    public MobilePlaylistSongDto MapToPlaylistSong(SongMetadata m, TimeSpan sasLifetime, int? userPlaylistId)
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
            TrackLengthSeconds = m.TrackLength,
            CreatorId = m.CreatorId,
            CreatorUserId = m.Creator?.UserId
        };
    }

    private static string ResolveTitle(SongMetadata m) =>
        !string.IsNullOrEmpty(m.SongTitle)
            ? m.SongTitle
            : Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? string.Empty);

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
}
