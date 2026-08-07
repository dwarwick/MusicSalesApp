<#
.SYNOPSIS
    Builds the audio-processing Function app's settings from the web app's appsettings file.

.DESCRIPTION
    Dot-source this from another script; it defines Get-MediaProcessingSettings and returns an
    ordered hashtable of setting name -> value.

    One source of truth on purpose: the storage connection strings already live in the web app's
    gitignored appsettings.{Environment}.json, and a second hand-maintained copy would drift.

        . "$PSScriptRoot\Get-MediaProcessingSettings.ps1"
        $values = Get-MediaProcessingSettings -Environment Test
#>

Set-StrictMode -Version 3.0

function Get-MediaProcessingSettingValue {
    param(
        [Parameter(Mandatory = $true)] [object]$Object,
        [Parameter(Mandatory = $true)] [string]$Section,
        [Parameter(Mandatory = $true)] [string]$Name
    )

    $sectionProperty = $Object.PSObject.Properties[$Section]
    if ($null -eq $sectionProperty) { return $null }

    $nameProperty = $sectionProperty.Value.PSObject.Properties[$Name]
    if ($null -eq $nameProperty) { return $null }

    return $nameProperty.Value
}

function Get-MediaProcessingSettings {
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

    function Require-Setting {
        param([string]$Value, [string]$Key)
        if ([string]::IsNullOrWhiteSpace($Value)) {
            throw "'$Key' is missing or empty in $SettingsPath. The Function cannot run without it."
        }
        return $Value
    }

    # The premium account holding song media. The Function reads this for the probe path and writes
    # cover-art renditions into it - derived artefacts only. The web app is still the sole writer of
    # every primary blob and of the database, so this is the same connection string either way and
    # no credential change was needed when renditions moved into the Function.
    $mediaConnection = Require-Setting (Get-MediaProcessingSettingValue $settings "Azure" "StorageAccountConnectionString") "Azure:StorageAccountConnectionString"
    $mediaContainer = Require-Setting (Get-MediaProcessingSettingValue $settings "Azure" "ContainerName") "Azure:ContainerName"

    # "AzureLowSpeed" is the standard general-purpose account. Premium accounts have no Queue
    # service at all, which is the entire reason there are two storage accounts - and why the
    # queues live on the account whose name refers to its tier.
    $stagingConnection = Require-Setting (Get-MediaProcessingSettingValue $settings "AzureLowSpeed" "StorageAccountConnectionString") "AzureLowSpeed:StorageAccountConnectionString"
    $stagingContainer = Require-Setting (Get-MediaProcessingSettingValue $settings "AzureLowSpeed" "UploadStagingContainerName") "AzureLowSpeed:UploadStagingContainerName"
    $transcodeQueue = Require-Setting (Get-MediaProcessingSettingValue $settings "AzureLowSpeed" "TranscodeQueueName") "AzureLowSpeed:TranscodeQueueName"
    $probeQueue = Require-Setting (Get-MediaProcessingSettingValue $settings "AzureLowSpeed" "ProbeQueueName") "AzureLowSpeed:ProbeQueueName"
    $matchQueue = Require-Setting (Get-MediaProcessingSettingValue $settings "AzureLowSpeed" "MatchQueueName") "AzureLowSpeed:MatchQueueName"

    $apiKeyProperty = $settings.PSObject.Properties["MediaProcessingApiKey"]
    $apiKey = if ($null -ne $apiKeyProperty) { $apiKeyProperty.Value } else { $null }
    $apiKey = Require-Setting $apiKey "MediaProcessingApiKey"

    $baseUrlProperty = $settings.PSObject.Properties["BaseUrl"]
    $callbackBaseUrl = if ($null -ne $baseUrlProperty) { $baseUrlProperty.Value } else { $null }
    if ([string]::IsNullOrWhiteSpace($callbackBaseUrl)) {
        throw "'BaseUrl' is missing from $SettingsPath. The Function posts its results there."
    }

    # Optional, unlike everything above. Without it the Function pairs cover art on filenames alone,
    # which is a worse answer rather than a broken one - so a missing key must not stop provisioning.
    $openAiKey = Get-MediaProcessingSettingValue $settings "OpenAI" "ApiKey"

    if ($mediaConnection -eq $stagingConnection) {
        Write-Warning ("Azure:StorageAccountConnectionString and AzureLowSpeed:StorageAccountConnectionString " +
            "are the same account. Media is expected to be on the premium account and the queues on " +
            "the standard one - check this is deliberate.")
    }

    $result = [ordered]@{
        "FUNCTIONS_WORKER_RUNTIME"             = "dotnet-isolated"

        # Functions runtime bookkeeping. Shares the standard account with the queues.
        "AzureWebJobsStorage"                  = $stagingConnection

        # Referenced by name from the QueueTrigger attributes, so the HOST resolves it - which is
        # why it has to be an app setting and cannot come from a worker-loaded config file.
        "StagingStorageConnectionString"       = $stagingConnection
        "MediaStorageConnectionString"         = $mediaConnection

        # Referenced as %...% inside the QueueTrigger attributes. Same constraint.
        "MediaProcessing:TranscodeQueueName"   = $transcodeQueue
        "MediaProcessing:ProbeQueueName"       = $probeQueue
        "MediaProcessing:MatchQueueName"       = $matchQueue

        "MediaProcessing:StagingContainerName" = $stagingContainer
        "MediaProcessing:MediaContainerName"   = $mediaContainer

        "CallbackBaseUrl"                      = $callbackBaseUrl
        "MediaProcessingApiKey"                = $apiKey
    }

    if (-not [string]::IsNullOrWhiteSpace($openAiKey)) {
        $result["OpenAI:ApiKey"] = $openAiKey
    }

    return $result
}
