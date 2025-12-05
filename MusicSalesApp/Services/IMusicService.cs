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
    }
}
