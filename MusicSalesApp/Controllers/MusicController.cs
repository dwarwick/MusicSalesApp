using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;
using System.IO;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
[IgnoreAntiforgeryToken] // Exempt all actions in this controller
public class MusicController : ControllerBase
{
    private readonly IAzureStorageService _storageService;
    private readonly IMusicService _musicService;
    private readonly ILogger<MusicController> _logger;

    public MusicController(IAzureStorageService storageService, IMusicService musicService, ILogger<MusicController> logger)
    {
        _storageService = storageService;
        _musicService = musicService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var files = await _storageService.ListFilesAsync();
        return Ok(files);
    }

    // Smooth streaming: return full blob stream and let ASP.NET Core + browser handle range requests.
    // Browser will issue subsequent Range headers (bytes=...) transparently for progressive playback.
    [HttpGet("{fileName}")]
    public async Task<IActionResult> Stream(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return BadRequest();
        var info = await _storageService.GetFileInfoAsync(fileName);
        if (info == null) return NotFound();

        var contentType = NormalizeContentType(info.ContentType, fileName);
        var stream = await _storageService.OpenReadAsync(fileName);
        if (stream == null) return NotFound();

        // enableRangeProcessing delegates partial content (206) responses automatically
        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpPost("upload")]
    [Authorize(Policy = Permissions.UploadFiles)]
    [RequestSizeLimit(200_000_000)] // 200 MB total request size
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000, ValueLengthLimit = 200_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string destinationFolder)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded" });

        if (string.IsNullOrWhiteSpace(destinationFolder))
            destinationFolder = string.Empty;

        try
        {
            await _storageService.EnsureContainerExistsAsync();

            using var fileStream = file.OpenReadStream();

            // Validate audio file
            if (!await _musicService.IsValidAudioFileAsync(fileStream, file.FileName))
            {
                return BadRequest(new { message = $"File {file.FileName} is not a valid audio file" });
            }

            fileStream.Position = 0;

            Stream uploadStream;
            string uploadFileName;

            // Convert to MP3 if needed
            if (!_musicService.IsMp3File(file.FileName))
            {
                _logger.LogInformation("Converting {FileName} to MP3", file.FileName);
                uploadStream = await _musicService.ConvertToMp3Async(fileStream, file.FileName);
                uploadFileName = Path.ChangeExtension(file.FileName, ".mp3");
            }
            else
            {
                uploadStream = fileStream;
                uploadFileName = file.FileName;
            }

            // Build full path with folder
            var fullPath = string.IsNullOrWhiteSpace(destinationFolder) 
                ? uploadFileName 
                : $"{destinationFolder.TrimEnd('/')}/{uploadFileName}";

            await _storageService.UploadAsync(fullPath, uploadStream, "audio/mpeg");

            if (uploadStream != fileStream)
            {
                await uploadStream.DisposeAsync();
            }

            return Ok(new { message = $"File {uploadFileName} uploaded successfully", fileName = fullPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file {FileName}", file.FileName);
            return StatusCode(500, new { message = $"Error uploading file: {ex.Message}" });
        }
    }

    private static string NormalizeContentType(string original, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(original) && original != "application/octet-stream") return original;
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
