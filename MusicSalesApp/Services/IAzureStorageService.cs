using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;

namespace MusicSalesApp.Services
{
    public interface IAzureStorageService
    {
        Task UploadAsync(string fileName, Stream data, string contentType);
        Task UploadAsync(string fileName, Stream data, string contentType, IDictionary<string, string> tags);
        Task<Stream> DownloadAsync(string fileName); // full download (legacy)
        Task<bool> DeleteAsync(string fileName);
        Task<bool> ExistsAsync(string fileName);
        Task<IEnumerable<StorageFileInfo>> ListFilesAsync();
        Task<PaginatedResult<StorageFileInfo>> ListFilesPagedAsync(int skip, int take);
        Task<StorageFileInfo> GetFileInfoAsync(string fileName); // null if not found
        Task<Stream> DownloadRangeAsync(string fileName, long? offset, long? length); // legacy manual range
        Task<Stream> OpenReadAsync(string fileName); // optimized streaming seekable stream (empty if not found)
        Task<Stream> DownloadSegmentAsync(string fileName, long start, long end); // slice via seek
        Task<Stream> DownloadRangeDirectAsync(string fileName, long start, long end); // direct range fetch via SDK
        Task EnsureContainerExistsAsync(); // ensure container exists

        Task<IEnumerable<StorageFileInfo>> ListFilesByAlbumAsync(string albumName);

        Uri GetReadSasUri(string fileName, TimeSpan lifetime);

        Task SetTagsAsync(string fileName, IDictionary<string, string> tags);
        Task<IDictionary<string, string>> GetTagsAsync(string fileName);
    }

    public class StorageFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public long Length { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public DateTimeOffset? LastModified { get; set; }
        /// <summary>
        /// The blob index tags associated with this file.
        /// </summary>
        public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class PaginatedResult<T>
    {
        public IEnumerable<T> Items { get; set; } = new List<T>();
        public int TotalCount { get; set; }
    }
}