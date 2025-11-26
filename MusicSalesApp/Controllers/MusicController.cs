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

        public MusicController(IAzureStorageService storageService)
        {
            _storageService = storageService;            
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
