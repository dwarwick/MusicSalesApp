using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Filename-pairing helpers for the upload page, plus the album-cover upload path.
    ///
    /// <para>
    /// The song upload methods that used to live here moved to <see cref="ISongUploadJobService"/>
    /// when FFmpeg processing moved to Azure Functions. A creator's audio is now staged and queued
    /// rather than transcoded inline, and the blobs are assembled into the song's GUID folder by
    /// <see cref="IMediaProcessingCompletionService"/> once the Function reports back. Nothing on
    /// this interface touches FFmpeg.
    /// </para>
    /// </summary>
    public interface IMusicUploadService
    {
        /// <summary>
        /// Uploads an album cover image file to storage with metadata indicating it is the album cover.
        /// </summary>
        Task<string> UploadAlbumCoverAsync(
            Stream albumArtStream,
            string albumArtFileName,
            string albumName,
            int? creatorId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that the given audio and album art filenames have the same base name.
        /// </summary>
        bool ValidateFilePairing(string audioFileName, string albumArtFileName);

        /// <summary>
        /// Gets the validated base name from a filename by removing its extension.
        /// </summary>
        string GetNormalizedBaseName(string fileName);

        /// <summary>
        /// Validates that all provided files have matching pairs (MP3 with JPEG/PNG).
        /// </summary>
        FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames, bool requireAudioFile = true);

        FilePairingValidationResult ValidateAllFilePairings(
            IEnumerable<string> fileNames,
            bool requireAudioFile,
            bool requireCoverArt);
    }

    /// <summary>
    /// Result of file pairing validation.
    /// </summary>
    public class FilePairingValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> UnmatchedMp3Files { get; set; } = new List<string>();
        public List<string> UnmatchedAlbumArtFiles { get; set; } = new List<string>();
    }
}
