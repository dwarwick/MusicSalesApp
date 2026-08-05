using System.Net.Http.Headers;
using FFMpegCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions;
using MusicSalesApp.Functions.Audio;
using MusicSalesApp.Functions.Services;

// No ConfigureFunctionsWebApplication: this app has no HTTP triggers. Both entry points are queue
// triggers, and the only outbound traffic is the callbacks to the site.
var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.Configure<FunctionOptions>(options =>
{
    var configuration = builder.Configuration;
    options.StagingStorageConnectionString = configuration["StagingStorageConnectionString"];
    options.MediaStorageConnectionString = configuration["MediaStorageConnectionString"];
    options.StagingContainerName = configuration["MediaProcessing:StagingContainerName"];
    options.MediaContainerName = configuration["MediaProcessing:MediaContainerName"];
    options.CallbackBaseUrl = configuration["CallbackBaseUrl"];
    options.MediaProcessingApiKey = configuration["MediaProcessingApiKey"];
});

builder.Services.AddSingleton<IMediaBlobStore, MediaBlobStore>();
builder.Services.AddSingleton<IFfmpegAudioProcessor, FfmpegAudioProcessor>();

builder.Services
    .AddHttpClient<IMediaProcessingCallbackClient, MediaProcessingCallbackClient>((provider, client) =>
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var baseUrl = configuration["CallbackBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("CallbackBaseUrl is not configured.");
        }

        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        var apiKey = configuration["MediaProcessingApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("MediaProcessingApiKey is not configured.");
        }

        client.DefaultRequestHeaders.Add(MediaProcessingRoutes.ApiKeyHeaderName, apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Sized for the longest call this client makes: the terminal callback, which the site
        // answers only once it has finished assembling the song. Two minutes was shorter than the
        // site's own assembly budget, so a slow-but-healthy assembly was abandoned here and the
        // queue redelivered on top of work that was still running. Progress posts do not inherit
        // this - they carry their own short per-request timeout.
        client.Timeout = MediaProcessingTimeouts.TerminalCallback;
    });

// FFmpeg lives in the deployment package next to the assemblies. Under
// WEBSITE_RUN_FROM_PACKAGE=1 that package is mounted READ-ONLY: running the binary from there is
// fine, but nothing may be written beside it. The web app pointed FFMpegCore's temporary folder at
// a directory under its content root, which would fail here - hence %TEMP%.
GlobalFFOptions.Configure(options =>
{
    options.BinaryFolder = AppContext.BaseDirectory;
    options.TemporaryFilesFolder = Path.GetTempPath();
});

builder.Build().Run();
