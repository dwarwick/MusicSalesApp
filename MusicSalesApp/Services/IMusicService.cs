using System.IO;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    public interface IMusicService
    {
        Task<bool> IsValidAudioFileAsync(Stream fileStream, string fileName);
        Task<Stream> ConvertToMp3Async(Stream inputStream, string originalFileName, IProgress<double> progress = null);
        bool IsMp3File(string fileName);
        Task<double?> GetAudioDurationAsync(Stream audioStream, string fileName);
        Task<AudioDecodeResult> ValidateAudioDecodeAsync(
            Stream audioStream,
            string fileName,
            CancellationToken cancellationToken = default);
    }

    public enum AudioDecodeStatus
    {
        Playable,
        Unplayable,
        Inconclusive
    }

    public sealed record AudioDecodeResult(
        AudioDecodeStatus Status,
        double? Duration,
        string FailureCode = null,
        string Diagnostic = null)
    {
        public static AudioDecodeResult Playable(double duration)
            => new(AudioDecodeStatus.Playable, duration);

        public static AudioDecodeResult Unplayable(string code, string diagnostic)
            => new(AudioDecodeStatus.Unplayable, null, code, diagnostic);

        public static AudioDecodeResult Inconclusive(string code, string diagnostic)
            => new(AudioDecodeStatus.Inconclusive, null, code, diagnostic);
    }
}
