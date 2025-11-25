using System.Threading.Tasks;
using System.IO;

namespace MusicSalesApp.Services
{
    public interface IAzureStorageService
    {
        Task UploadAsync(string fileName, Stream data, string contentType);
        Task<Stream> DownloadAsync(string fileName); // returns empty stream if not found
        Task<bool> DeleteAsync(string fileName);
        Task<bool> ExistsAsync(string fileName);
    }
}