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
    }
}