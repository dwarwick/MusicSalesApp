using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MusicSalesApp.Services
{
    public interface IMusicUploadService
    {
        /// <summary>
        /// Validates and uploads an audio file to storage, converting to MP3 if necessary.
        /// Used from MVC controllers where files arrive as IFormFile.
        /// </summary>
        Task<string> UploadAudioAsync(
            IFormFile file,
            string destinationFolder,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates and uploads an audio file to storage, converting to MP3 if necessary.
        /// Used from Blazor components where files are IBrowserFile streams.
        /// </summary>
        Task<string> UploadAudioAsync(
            Stream fileStream,
            string originalFileName,
            string destinationFolder,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads validated audio and paired album art to storage.
        /// The validated original audio is retained and a playback MP3 is created when needed.
        /// Files are stored in a folder named after the validated base filename.
        /// </summary>
        /// <param name="audioStream">The MP3 audio file stream.</param>
        /// <param name="audioFileName">Original filename of the MP3 file.</param>
        /// <param name="albumArtStream">The album art file stream (JPEG or PNG).</param>
        /// <param name="albumArtFileName">Original filename of the album art file.</param>
        /// <param name="albumName">Optional album name to store as metadata.</param>
        /// <param name="creatorId">Optional creator ID if uploaded by a creator.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The folder path where files were stored.</returns>
        Task<MusicUploadResult> UploadMusicWithAlbumArtAsync(
            Stream audioStream,
            string audioFileName,
            Stream albumArtStream,
            string albumArtFileName,
            string albumName = null,
            int? creatorId = null,
            string storageBaseName = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads validated audio without cover art, retaining the original source and
        /// creating a playback MP3 when needed. Files are stored in a folder named after
        /// the validated base filename.
        /// </summary>
        /// <param name="audioStream">The audio file stream.</param>
        /// <param name="audioFileName">Original filename of the audio file.</param>
        /// <param name="albumName">Optional album name to store as metadata.</param>
        /// <param name="creatorId">Optional creator ID if uploaded by a creator.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The folder path where the file was stored.</returns>
        Task<MusicUploadResult> UploadMusicWithoutAlbumArtAsync(
            Stream audioStream,
            string audioFileName,
            string albumName = null,
            int? creatorId = null,
            string storageBaseName = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads an album cover image file to storage with metadata indicating it is the album cover.
        /// </summary>
        /// <param name="albumArtStream">The album art file stream (JPEG or PNG).</param>
        /// <param name="albumArtFileName">Original filename of the album art file.</param>
        /// <param name="albumName">The album name to store as metadata.</param>
        /// <param name="creatorId">Optional creator ID if uploaded by a creator.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The path where the album cover was stored.</returns>
        Task<string> UploadAlbumCoverAsync(
            Stream albumArtStream,
            string albumArtFileName,
            string albumName,
            int? creatorId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that the given audio and album art filenames have the same base name.
        /// </summary>
        /// <param name="audioFileName">The MP3 filename.</param>
        /// <param name="albumArtFileName">The album art filename.</param>
        /// <returns>True if filenames match, false otherwise.</returns>
        bool ValidateFilePairing(string audioFileName, string albumArtFileName);

        /// <summary>
        /// Gets the validated base name from a filename by removing its extension.
        /// </summary>
        /// <param name="fileName">The filename to normalize.</param>
        /// <returns>The normalized base name.</returns>
        string GetNormalizedBaseName(string fileName);

        /// <summary>
        /// Validates that all provided files have matching pairs (MP3 with JPEG/PNG).
        /// </summary>
        /// <param name="fileNames">List of filenames to validate.</param>
        /// <param name="requireAudioFile">If true, requires at least one audio file. Defaults to true.</param>
        /// <returns>A result containing unmatched files if validation fails.</returns>
        FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames, bool requireAudioFile = true);

        FilePairingValidationResult ValidateAllFilePairings(
            IEnumerable<string> fileNames,
            bool requireAudioFile,
            bool requireCoverArt);
    }

    public sealed class MusicUploadResult
    {
        public string FolderPath { get; init; } = string.Empty;
        public string OriginalAudioBlobPath { get; init; } = string.Empty;
        public string OriginalAudioFileName { get; init; } = string.Empty;
        public long OriginalAudioFileSize { get; init; }
        public string OriginalAudioContentType { get; init; } = string.Empty;
        public string Mp3BlobPath { get; init; } = string.Empty;
        public string ImageBlobPath { get; init; }
        public double TrackDuration { get; init; }
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
