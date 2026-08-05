using System.IO;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Header-level checks on audio files.
    ///
    /// <para>
    /// Everything that needed FFmpeg - transcoding, decode validation, duration extraction - moved to
    /// the <c>MusicSalesApp.Functions</c> Azure Functions app. This host runs on shared hosting and
    /// no longer has an ffmpeg binary at all, so what is left here is the cheap magic-byte gate that
    /// can safely run on a request thread.
    /// </para>
    /// </summary>
    public interface IMusicService
    {
        /// <summary>
        /// Whether the stream's actual container matches its extension. Header inspection only: it
        /// catches a renamed or truncated-at-byte-zero file instantly, but proving the audio decodes
        /// end to end is the Function's job.
        /// </summary>
        Task<bool> IsValidAudioFileAsync(Stream fileStream, string fileName);

        bool IsMp3File(string fileName);
    }
}
