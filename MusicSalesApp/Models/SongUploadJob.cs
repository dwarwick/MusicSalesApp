using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Models;

public enum SongUploadJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

/// <summary>
/// One creator upload in flight, from the moment its bytes are staged until the song is live.
///
/// <para>
/// This table exists so that <see cref="SongMetadata"/> is written <em>only</em> on success. FFmpeg
/// now runs in an Azure Function, so there is a window of seconds to minutes where a song is
/// half-built; parking that state here rather than in a partially-populated SongMetadata row means
/// the catalogue, the playlists and the mobile API cannot observe a song that has no playable
/// audio, and no query anywhere has to remember to filter one out.
/// </para>
///
/// <para>
/// <see cref="MediaGuid"/> is minted when the job is created and is used end to end - it names the
/// staging folder, correlates every queue message and callback, and finally names the song's folder
/// in the media container. There is no second correlation key to keep in sync.
/// </para>
/// </summary>
public class SongUploadJob
{
    [Key]
    public int Id { get; set; }

    /// <summary>The song's media GUID, minted up front. Also the staging folder name.</summary>
    public Guid MediaGuid { get; set; }

    public int CreatorId { get; set; }

    public virtual Creator Creator { get; set; }

    /// <summary>Validated before the job was created, so it is already known to be usable.</summary>
    [MaxLength(200)]
    public string SongTitle { get; set; }

    [MaxLength(200)]
    public string AlbumName { get; set; }

    /// <summary>
    /// The genre the creator chose at the review step. Required for a song to publish, which is
    /// why it rides along here rather than being asked for afterwards.
    /// </summary>
    public string Genre { get; set; }

    /// <summary>The persona to credit, or null to fall back to the creator display name.</summary>
    public int? PersonaId { get; set; }

    /// <summary>AI disclosure, stated when the file is handed over rather than added later.</summary>
    public bool IsAiGenerated { get; set; }

    public bool IsAiVocals { get; set; }

    public bool IsAiLyrics { get; set; }

    /// <summary>Path within the staging container, e.g. <c>{guid}/source.wav</c>.</summary>
    [MaxLength(500)]
    public string SourceBlobPath { get; set; }

    /// <summary>The creator's own filename, retained as data exactly as the upload flow always has.</summary>
    [MaxLength(255)]
    public string SourceFileName { get; set; }

    [MaxLength(10)]
    public string SourceExtension { get; set; }

    public long SourceFileSize { get; set; }

    [MaxLength(100)]
    public string SourceContentType { get; set; }

    /// <summary>Null when the creator uploaded no cover art.</summary>
    [MaxLength(500)]
    public string CoverArtBlobPath { get; set; }

    [MaxLength(255)]
    public string CoverArtFileName { get; set; }

    [MaxLength(10)]
    public string CoverArtExtension { get; set; }

    /// <summary>Where the Function wrote the playback MP3, within the staging container.</summary>
    [MaxLength(500)]
    public string PlaybackBlobPath { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>
    /// True when the source was already MP3 and no transcode ran, so the original and the playback
    /// copy are the same bytes and only one blob is stored.
    /// </summary>
    public bool SourceWasAlreadyMp3 { get; set; }

    public SongUploadJobStatus Status { get; set; } = SongUploadJobStatus.Queued;

    /// <summary>
    /// How far the pipeline has got. Persisted - rather than only broadcast - so a creator who
    /// refreshes the page picks the bar back up at the right place, and so the reconciler can spot
    /// a job that has stopped moving rather than merely one that has been around a while.
    /// </summary>
    public AudioProcessingStep Step { get; set; } = AudioProcessingStep.Staging;

    /// <summary>
    /// When <see cref="Step"/> last changed. Only step transitions touch this row; the far more
    /// frequent percentage updates ride SignalR alone and are never written to the database.
    /// </summary>
    public DateTime StepUpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string FailureCode { get; set; }

    [MaxLength(2000)]
    public string FailureMessage { get; set; }

    /// <summary>Set once assembly succeeds. Null for everything that has not finished.</summary>
    public int? SongMetadataId { get; set; }

    public virtual SongMetadata SongMetadata { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
