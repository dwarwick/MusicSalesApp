<#
.SYNOPSIS
    Builds the lyrics-alignment Function app's settings from the web app's appsettings file.

.DESCRIPTION
    Dot-source this from another script; it defines Get-LyricsProcessingSettings and returns an
    ordered hashtable of setting name -> value.

    A sibling of Get-MediaProcessingSettings.ps1 rather than a parameter on it, and the reasoning is
    worth stating because both options look reasonable.

    That function's result feeds TWO things: the settings push, and the drift-check baseline in
    Provision-FunctionApp.ps1. Adding lyrics-only keys to its single flat hashtable would make the
    C# app's drift check report them as missing forever, on every run, for an app that should never
    have them.

    But it is not a fork either, which is the other way to get this wrong. The web app's
    appsettings.{Environment}.json is still the only place the storage connection strings live, and
    the parsing helper is reused from the file this dot-sources. There is one source of truth; there
    are two shapes read off it, because there are two Function apps with different needs.

        . "$PSScriptRoot\Get-LyricsProcessingSettings.ps1"
        $values = Get-LyricsProcessingSettings -Environment Test
#>

Set-StrictMode -Version 3.0

. "$PSScriptRoot\Get-MediaProcessingSettings.ps1"

function Get-LyricsProcessingSettings {
    [CmdletBinding()]
    param(
        [ValidateSet("Development", "Test", "Production")]
        [string]$Environment = "Development",

        [string]$SettingsPath,

        [string]$RepositoryRoot = $PSScriptRoot
    )

    if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
        $SettingsPath = Join-Path $RepositoryRoot "MusicSalesApp\appsettings.$Environment.json"
    }

    if (-not (Test-Path -LiteralPath $SettingsPath)) {
        throw "Settings file was not found: $SettingsPath"
    }

    $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json

    function Require-LyricsSetting {
        param([string]$Value, [string]$Key)
        if ([string]::IsNullOrWhiteSpace($Value)) {
            throw "'$Key' is missing or empty in $SettingsPath. The lyrics Function cannot run without it."
        }
        return $Value
    }

    # Same accessor the audio settings use, so a section that is present-but-empty is treated
    # identically by both.
    $mediaConnection = Require-LyricsSetting `
        (Get-MediaProcessingSettingValue -Object $settings -Section "Azure" -Name "StorageAccountConnectionString") `
        "Azure:StorageAccountConnectionString"

    $mediaContainer = Require-LyricsSetting `
        (Get-MediaProcessingSettingValue -Object $settings -Section "Azure" -Name "ContainerName") `
        "Azure:ContainerName"

    $stagingConnection = Require-LyricsSetting `
        (Get-MediaProcessingSettingValue -Object $settings -Section "AzureLowSpeed" -Name "StorageAccountConnectionString") `
        "AzureLowSpeed:StorageAccountConnectionString"

    $stagingContainer = Require-LyricsSetting `
        (Get-MediaProcessingSettingValue -Object $settings -Section "AzureLowSpeed" -Name "UploadStagingContainerName") `
        "AzureLowSpeed:UploadStagingContainerName"

    $baseUrlProperty = $settings.PSObject.Properties["BaseUrl"]
    $callbackBaseUrl = Require-LyricsSetting `
        ($(if ($null -eq $baseUrlProperty) { $null } else { $baseUrlProperty.Value })) `
        "BaseUrl"

    $apiKeyProperty = $settings.PSObject.Properties["MediaProcessingApiKey"]
    $apiKey = Require-LyricsSetting `
        ($(if ($null -eq $apiKeyProperty) { $null } else { $apiKeyProperty.Value })) `
        "MediaProcessingApiKey"

    # Per-environment, and NOT optional. Test and Production share one storage account, so a shared
    # hub name would put both environments on the same task hub, where they would pick up each
    # other's orchestrations. Derived from the environment rather than configured, so it cannot be
    # forgotten.
    #
    # Named the way every other per-environment resource in this repository is named: production is
    # the bare name and the others are suffixed - audio-transcode / -dev / -local, musiccontainer /
    # -dev / -local. Task hub names are alphanumeric only, so the suffixes lose their hyphen, but
    # the shape is the same and production stays unadorned.
    $taskHubName = switch ($Environment) {
        "Production" { "LyricsAlignHub" }
        "Test" { "LyricsAlignHubDev" }
        default { "LyricsAlignHubLocal" }
    }

    $result = [ordered]@{
        "FUNCTIONS_WORKER_RUNTIME"             = "python"
        "FUNCTIONS_EXTENSION_VERSION"          = "~4"

        # Functions runtime bookkeeping plus the Durable task hub. Shares the standard account with
        # the staging container - the media account is Premium and offers no Queue service at all.
        "AzureWebJobsStorage"                  = $stagingConnection
        "StagingStorageConnectionString"       = $stagingConnection
        "MediaStorageConnectionString"         = $mediaConnection

        # Double underscore, not the colon the C# Function app uses for the same two values.
        # Flex Consumption REJECTS an app setting whose name contains a colon outright - not a
        # warning, the whole `appsettings set` call fails - and `__` is the documented separator on
        # Linux App Service. The Python app reads these as flat environment variables anyway, so the
        # colon was only ever there to mirror the .NET app's hierarchical config binding.
        "MediaProcessing__StagingContainerName" = $stagingContainer
        "MediaProcessing__MediaContainerName"   = $mediaContainer

        # Referenced as %LyricsTaskHubName% from host.json, so the HOST resolves it before the worker
        # starts. Missing, the whole app fails to start - the same trap MediaProcessing:MatchQueueName
        # already sprang once on the C# app.
        "LyricsTaskHubName"                    = $taskHubName

        "CallbackBaseUrl"                      = $callbackBaseUrl
        "MediaProcessingApiKey"                = $apiKey

        # Model weights and the Linux ffmpeg live on an Azure Files mount, not in the deployment
        # package: CPU-only torch is already several hundred megabytes and the weights are more than
        # a gigabyte on top. Flex is zip-deploy, so they would not fit.
        "FFMPEG_BINARY"                        = "/mnt/tools/ffmpeg"
        "TORCH_HOME"                           = "/mnt/models/torch"
        "XDG_CACHE_HOME"                       = "/mnt/models"

        # Peak Demucs memory scales with segment length, and an out-of-memory kill is a hard failure:
        # the orchestrator deliberately does not retry separation, because a retry at the same
        # instance size fails the same way after spending the same minutes.
        #
        # SEVEN, AND IT CANNOT GO ABOVE 7.8. htdemucs is a hybrid TRANSFORMER, and a transformer
        # cannot be run at a longer segment than it was trained for - Demucs refuses outright with
        # "FATAL: Cannot use a Transformer model with a longer segment than it was trained for.
        # Maximum segment is: 7.8". This setting was 8 and every alignment died there, after paying
        # for the model download first. Note the direction is the opposite of the memory concern
        # above: too LARGE fails instantly and always, too small only costs a little quality.
        # Raising it needs a non-transformer model (mdx_extra and friends have no such cap).
        "DEMUCS_SEGMENT"                       = "7"

        # Demucs' peak memory is driven by the length of the WHOLE TRACK, not by the segment above:
        # it holds full-length tensors for all four sources. Flex Consumption accepts only 512, 2048
        # and 4096 MB - 8192 is rejected by name - so there is no bigger instance to escape to, and a
        # long track has to be separated in pieces or it is SIGKILLed part way through with no
        # traceback. Measured on this 4096 MB app: a 29 s clip separates comfortably, the 3 min 41 s
        # track it was cut from does not, and halving DEMUCS_SEGMENT changed nothing either way.
        #
        # The margin is separated either side of each chunk and then discarded, so the model sees real
        # audio as context across every boundary rather than silence.
        "DEMUCS_CHUNK_SECONDS"                 = "30"
        "DEMUCS_CHUNK_MARGIN_SECONDS"          = "5"

        # Four, which is what a 4096 MB Flex instance actually reports - measured from the deployed
        # app, not assumed. This was originally 2 on the guess that the top Flex tier allocated two
        # cores; it does not, and Demucs is CPU-bound, so half the cores being paid for were sitting
        # idle through the longest stage of every run.
        #
        # Coupled to -InstanceMemoryMb 4096 in Provision-LyricsFunctionApp.ps1. A smaller instance
        # gets fewer cores, and over-subscribing them costs more in contention than it buys.
        # Left unset entirely, torch sizes its own pool from what it thinks it sees, which in a
        # container is usually the host's core count rather than the share this app is entitled to.
        "OMP_NUM_THREADS"                      = "4"
        "MKL_NUM_THREADS"                      = "4"

        "PYTHONUNBUFFERED"                     = "1"
    }

    # Deliberately NOT set: WEBSITE_RUN_FROM_PACKAGE. The C# app needs it and Flex Consumption
    # breaks with it - Flex has its own one-deploy mechanism. This comment exists because copying
    # that line across from Provision-FunctionApp.ps1 is the single easiest mistake to make here.

    return $result
}
