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
        "DEMUCS_SEGMENT"                       = "8"

        # Two, matching the cores a 4096 MB Flex instance gets. Left unset, torch spawns as many
        # threads as it thinks it sees and spends the difference on contention.
        "OMP_NUM_THREADS"                      = "2"
        "MKL_NUM_THREADS"                      = "2"

        "PYTHONUNBUFFERED"                     = "1"
    }

    # Deliberately NOT set: WEBSITE_RUN_FROM_PACKAGE. The C# app needs it and Flex Consumption
    # breaks with it - Flex has its own one-deploy mechanism. This comment exists because copying
    # that line across from Provision-FunctionApp.ps1 is the single easiest mistake to make here.

    return $result
}
