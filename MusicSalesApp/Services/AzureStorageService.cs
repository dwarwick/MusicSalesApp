using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    public class AzureStorageService : IAzureStorageService
    {
        private readonly BlobContainerClient _containerClient;
        private readonly ILogger<AzureStorageService> _logger;

        public AzureStorageService(IOptions<AzureStorageOptions> options, ILogger<AzureStorageService> logger)
        {
            _logger = logger;
            var opts = options.Value;
            if (string.IsNullOrWhiteSpace(opts.StorageAccountConnectionString))
                throw new ArgumentException("StorageAccountConnectionString configuration missing.");
            if (string.IsNullOrWhiteSpace(opts.ContainerName))
                throw new ArgumentException("ContainerName configuration missing.");

            _containerClient = new BlobContainerClient(opts.StorageAccountConnectionString, opts.ContainerName);
        }

        public async Task EnsureContainerExistsAsync()
        {
            try
            {
                await _containerClient.CreateIfNotExistsAsync();
                _logger.LogInformation("Container {ContainerName} exists or was created", _containerClient.Name);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed ensuring container exists");
                throw;
            }
        }

        public async Task UploadAsync(string fileName, Stream data, string contentType)
        {
            await UploadAsync(fileName, data, contentType, null);
        }

        public async Task UploadAsync(string fileName, Stream data, string contentType, IDictionary<string, string> tags)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            if (data == null) throw new ArgumentNullException(nameof(data));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                var headers = new BlobHttpHeaders { ContentType = contentType };
                if (data.CanSeek)
                {
                    data.Position = 0;
                }
                var uploadOptions = new BlobUploadOptions { HttpHeaders = headers };
                await blobClient.UploadAsync(data, uploadOptions);

                // Set index tags after upload if provided
                if (tags != null && tags.Count > 0)
                {
                    await blobClient.SetTagsAsync(tags);
                }
                _logger.LogInformation("Uploaded blob {FileName} ({Length} bytes).", fileName, data.Length);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed uploading blob {FileName}", fileName);
                throw;
            }
        }

        public async Task<Stream> DownloadAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!await blobClient.ExistsAsync())
                {
                    _logger.LogWarning("Blob {FileName} not found for download.", fileName);
                    return new MemoryStream();
                }
                var ms = new MemoryStream();
                var response = await blobClient.DownloadAsync();
                await response.Value.Content.CopyToAsync(ms);
                ms.Position = 0;
                _logger.LogInformation("Downloaded blob {FileName} ({Length} bytes).", fileName, ms.Length);
                return ms;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed downloading blob {FileName}", fileName);
                throw;
            }
        }

        public async Task<bool> DeleteAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                var result = await blobClient.DeleteIfExistsAsync();
                if (result) _logger.LogInformation("Deleted blob {FileName}.", fileName);
                else _logger.LogWarning("Blob {FileName} not found to delete.", fileName);
                return result;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed deleting blob {FileName}", fileName);
                throw;
            }
        }

        public async Task<bool> ExistsAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                var exists = await blobClient.ExistsAsync();
                return exists.Value;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed checking existence for blob {FileName}", fileName);
                throw;
            }
        }

        public async Task<IEnumerable<StorageFileInfo>> ListFilesAsync()
        {
            var list = new List<StorageFileInfo>();
            try
            {
                await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.Tags))
                {
                    list.Add(new StorageFileInfo
                    {
                        Name = blobItem.Name,
                        Length = blobItem.Properties.ContentLength ?? 0,
                        ContentType = blobItem.Properties.ContentType ?? "application/octet-stream",
                        LastModified = blobItem.Properties.LastModified,
                        Tags = blobItem.Tags != null ? new Dictionary<string, string>(blobItem.Tags) : new Dictionary<string, string>()
                    });
                }
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed listing blobs");
                throw;
            }
            return list.OrderBy(b => b.Name);
        }

        // New: List blobs for a specific album using index tags
        public async Task<IEnumerable<StorageFileInfo>> ListFilesByAlbumAsync(string albumName)
        {
            if (string.IsNullOrWhiteSpace(albumName))
                throw new ArgumentNullException(nameof(albumName));

            var list = new List<StorageFileInfo>();

            try
            {
                // Escape single quotes for the tag query
                var safeAlbumName = albumName.Replace("'", "''");

                // Correct tag query: NO leading @
                var expression = $"\"AlbumName\" = '{safeAlbumName}'";

                await foreach (var taggedBlob in _containerClient.FindBlobsByTagsAsync(expression))
                {
                    var blobClient = _containerClient.GetBlobClient(taggedBlob.BlobName);

                    // Fetch properties and *all* tags for this blob
                    var propsTask = blobClient.GetPropertiesAsync();
                    var tagsTask = blobClient.GetTagsAsync();

                    await Task.WhenAll(propsTask, tagsTask);

                    var props = propsTask.Result;
                    var tags = tagsTask.Result;

                    list.Add(new StorageFileInfo
                    {
                        Name = taggedBlob.BlobName,
                        Length = props.Value.ContentLength,
                        ContentType = props.Value.ContentType ?? "application/octet-stream",
                        LastModified = props.Value.LastModified,
                        // Use the full tag set from GetTagsAsync, not taggedBlob.Tags
                        Tags = tags.Value.Tags != null
                            ? new Dictionary<string, string>(tags.Value.Tags)
                            : new Dictionary<string, string>()
                    });
                }
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed listing blobs by album {AlbumName}", albumName);
                throw;
            }

            return list.OrderBy(b => b.Name);
        }


        public async Task<StorageFileInfo> GetFileInfoAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!(await blobClient.ExistsAsync())) return null;
                var props = await blobClient.GetPropertiesAsync();
                var tags = await blobClient.GetTagsAsync();
                return new StorageFileInfo
                {
                    Name = fileName,
                    Length = props.Value.ContentLength,
                    ContentType = props.Value.ContentType ?? "application/octet-stream",
                    LastModified = props.Value.LastModified,
                    Tags = tags.Value.Tags != null ? new Dictionary<string, string>(tags.Value.Tags) : new Dictionary<string, string>()
                };
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed getting blob info {FileName}", fileName);
                throw;
            }
        }

        public async Task<Stream> DownloadRangeAsync(string fileName, long? offset, long? length)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!(await blobClient.ExistsAsync())) return new MemoryStream();
                var response = await blobClient.DownloadAsync();
                var full = new MemoryStream();
                await response.Value.Content.CopyToAsync(full);
                if (!offset.HasValue) { full.Position = 0; return full; }
                int start = (int)offset.Value;
                int sliceLen = (int)(length.HasValue ? length.Value : (full.Length - start));
                if (sliceLen <= 0) return new MemoryStream();
                var slice = new MemoryStream();
                full.Position = start;
                var buffer = new byte[81920];
                long remaining = sliceLen;
                while (remaining > 0)
                {
                    int read = full.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    slice.Write(buffer, 0, read);
                    remaining -= read;
                }
                slice.Position = 0;
                return slice;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed downloading blob range {FileName}", fileName);
                throw;
            }
        }

        public async Task<Stream> OpenReadAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!(await blobClient.ExistsAsync())) return new MemoryStream();
                var stream = await blobClient.OpenReadAsync();
                return stream;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed opening read stream for blob {FileName}", fileName);
                throw;
            }
        }

        public async Task<Stream> DownloadSegmentAsync(string fileName, long start, long end)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!(await blobClient.ExistsAsync())) return new MemoryStream();
                var length = end - start + 1;
                var fullStream = await blobClient.OpenReadAsync();
                if (!fullStream.CanSeek)
                {
                    var fallback = await blobClient.DownloadAsync();
                    var temp = new MemoryStream();
                    await fallback.Value.Content.CopyToAsync(temp);
                    temp.Position = start;
                    var segment = new MemoryStream();
                    var buffer = new byte[81920];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int read = temp.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) break;
                        segment.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    segment.Position = 0;
                    return segment;
                }
                fullStream.Seek(start, SeekOrigin.Begin);
                var slice = new MemoryStream();
                var buf = new byte[81920];
                long left = length;
                while (left > 0)
                {
                    int read = fullStream.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (read <= 0) break;
                    slice.Write(buf, 0, read);
                    left -= read;
                }
                slice.Position = 0;
                return slice;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed downloading segment {Start}-{End} for blob {FileName}", start, end, fileName);
                throw;
            }
        }

        public async Task<Stream> DownloadRangeDirectAsync(string fileName, long start, long end)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                if (!(await blobClient.ExistsAsync())) return new MemoryStream();

                var length = end - start + 1;
                var full = await blobClient.OpenReadAsync();
                if (!full.CanSeek) return new MemoryStream();
                full.Seek(start, SeekOrigin.Begin);

                var ms = new MemoryStream();
                var buffer = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = full.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    remaining -= read;
                }
                ms.Position = 0;
                return ms;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed direct range {Start}-{End} for blob {FileName}", start, end, fileName);
                throw;
            }
        }

        // New: generate a short-lived SAS URL for direct browser streaming
        public Uri GetReadSasUri(string fileName, TimeSpan lifetime)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));

            var blobClient = _containerClient.GetBlobClient(fileName);

            if (!blobClient.CanGenerateSasUri)
            {
                throw new InvalidOperationException("BlobClient cannot generate SAS URIs. Ensure a key-based connection string is used.");
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _containerClient.Name,
                BlobName = fileName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            return blobClient.GenerateSasUri(sasBuilder);
        }
    }
}