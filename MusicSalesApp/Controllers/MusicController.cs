using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MusicController : ControllerBase
    {
        private readonly IAzureStorageService _storageService;
        private readonly IMusicService _musicService;
        private readonly IMusicUploadService _uploadService;
        private readonly ILogger<MusicController> _logger;

        public MusicController(
            IAzureStorageService storageService,
            IMusicService musicService,
            IMusicUploadService uploadService,
            ILogger<MusicController> logger)
        {
            _storageService = storageService;
            _musicService = musicService;
            _uploadService = uploadService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var files = await _storageService.ListFilesAsync();
            return Ok(files);
        }

        [HttpGet("{fileName}")]
        public async Task<IActionResult> Stream(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            var info = await _storageService.GetFileInfoAsync(fileName);
            if (info == null)
                return NotFound();

            var contentType = NormalizeContentType(info.ContentType, fileName);
            var stream = await _storageService.OpenReadAsync(fileName);
            if (stream == null)
                return NotFound();

            return File(stream, contentType, enableRangeProcessing: true);
        }

        [HttpPost("upload")]
        // You can re-enable this later if you want API-level auth:
        // [Authorize(Policy = Permissions.UploadFiles)]
        [RequestSizeLimit(200_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000, ValueLengthLimit = 200_000_000)]
        public async Task<IActionResult> Upload(
            [FromForm] IFormFile file,
            [FromForm] string destinationFolder)
        {
            try
            {
                var fullPath = await _uploadService.UploadAudioAsync(
                    file,
                    destinationFolder,
                    HttpContext.RequestAborted);

                var fileNameOnly = Path.GetFileName(fullPath);

                return Ok(new
                {
                    message = $"File {fileNameOnly} uploaded successfully",
                    fileName = fullPath
                });
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Invalid audio file {FileName}", file?.FileName);
                return BadRequest(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Bad upload request for file {FileName}", file?.FileName);
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file {FileName}", file?.FileName);
                return StatusCode(500, new { message = $"Error uploading file: {ex.Message}" });
            }
        }

        private static string NormalizeContentType(string original, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(original) && original != "application/octet-stream")
                return original;

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                _ => "application/octet-stream"
            };
        }
    }
}
