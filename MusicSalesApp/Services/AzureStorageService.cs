using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
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
            _containerClient.CreateIfNotExists(PublicAccessType.None);
        }

        public async Task UploadAsync(string fileName, Stream data, string contentType)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
            if (data == null) throw new ArgumentNullException(nameof(data));
            try
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                var headers = new BlobHttpHeaders { ContentType = contentType };
                data.Position = 0;
                await blobClient.UploadAsync(data, new BlobUploadOptions { HttpHeaders = headers });
                _logger.LogInformation("Uploaded blob {FileName} ({Length} bytes).", fileName, data.Length);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure request failed uploading blob {FileName}", fileName);
                throw; // propagate for higher-level handling
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
                if (result) _logger.LogInformation("Deleted blob {FileName}.", fileName); else _logger.LogWarning("Blob {FileName} not found to delete.", fileName);
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
    }
}