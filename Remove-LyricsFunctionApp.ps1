<#
.SYNOPSIS
    Tears the lyrics-alignment Function app down, so provisioning it can be rehearsed.

.DESCRIPTION
    The sibling of Remove-FunctionApp.ps1. Its whole reason for existing is that
    Provision-LyricsFunctionApp.ps1 claims to be idempotent, and the only way to keep that claim
    honest is to be able to delete an environment and build it again.

    It deletes rather more than the obvious things, and the extra ones are the point.

    **The Durable task hub leaves resources behind that carry no hint of what they belong to.**
    A hub named LyricsAlignHubDev creates LyricsAlignHubDevHistory and LyricsAlignHubDevInstances
    tables, LyricsAlignHubDev-control-00..03 and -workitems queues, and a lyricsalignhubdev-leases
    container - all on the shared standard storage account, alongside the audio pipeline's queues and
    the staging container. Delete the Function app alone and every one of those survives.
    Re-provisioning then appears to work and behaves strangely, because the new app adopts a hub
    still holding the old one's orchestration history.

    The same applies to *renaming* a hub, which is why the names were settled before anything was
    provisioned: changing LyricsTaskHubName orphans the previous hub's resources exactly as deleting
    the app does, and nothing afterwards will ever tidy them up.

.EXAMPLE
    pwsh ./Remove-LyricsFunctionApp.ps1 -Environment Test -WhatIf
    pwsh ./Remove-LyricsFunctionApp.ps1 -Environment Test -IncludeTaskHub
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Test", "Production")]
    [string]$Environment,

    [string]$Subscription = "WebsitesSubscription",

    [string]$FunctionAppName,

    [string]$ResourceGroup,

    # The task hub holds in-flight orchestrations. Left alone by default so a teardown cannot
    # silently discard alignment runs somebody is waiting on - the same caution
    # Remove-FunctionApp.ps1 applies to the audio queues and staging.
    [switch]$IncludeTaskHub,

    # The mounted shares hold a Linux ffmpeg build and well over a gigabyte of model weights. Both
    # are slow to re-upload and neither contains anything a creator produced, so they survive a
    # teardown unless asked for explicitly.
    [switch]$IncludeSharedFiles
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "AzureCli.ps1")
. (Join-Path $PSScriptRoot "Get-LyricsProcessingSettings.ps1")

$account = Connect-AzureCliSession -Subscription $Subscription

if ([string]::IsNullOrWhiteSpace($FunctionAppName)) {
    $FunctionAppName = if ($Environment -eq "Production") { "streamtunes-lyrics-prod" } else { "streamtunes-lyrics-test" }
}

$values = Get-LyricsProcessingSettings -Environment $Environment -RepositoryRoot $PSScriptRoot
$stagingConnection = $values["StagingStorageConnectionString"]
$taskHubName = $values["LyricsTaskHubName"]

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    # `functionapp list` filtered by name, NOT `functionapp show`. show REQUIRES --resource-group,
    # which is the very thing being looked up - and it fails with "(--resource-group | --ids) are
    # required", which behind -AllowFailure was being read as "the app does not exist". The teardown
    # then reported nothing to remove and exited cleanly while the app was still running.
    $ResourceGroup = Invoke-Az @(
        "functionapp", "list",
        "--query", "[?name=='$FunctionAppName'].resourceGroup | [0]",
        "--output", "tsv") -AllowFailure

    $ResourceGroup = ($ResourceGroup | Out-String).Trim()

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResourceGroup)) {
        Write-Host "$FunctionAppName does not exist in subscription '$($account.name)'. Nothing to remove." -ForegroundColor Yellow
        return
    }
}

Write-Host "Function app  : $FunctionAppName"
Write-Host "Resource group: $ResourceGroup"
Write-Host "Task hub      : $taskHubName"

# Does the OTHER environment's app still exist?
#
# Asked once, because more than one thing below is shared with it and the list is not obvious.
# Test and Production resolve StagingStorageConnectionString to the same storage account, and
# Provision-LyricsFunctionApp.ps1 names several resources with bare literals - no environment
# suffix - so both apps end up pointed at the same 'lyrics-deployment' container and the same
# 'lyrics-models'/'lyrics-tools' shares. Only the task hub is suffixed per environment, and only
# because sharing THAT one corrupts orchestration state loudly rather than quietly.
$otherEnvironment = if ($Environment -eq "Production") { "Test" } else { "Production" }
$otherAppName = if ($otherEnvironment -eq "Production") { "streamtunes-lyrics-prod" } else { "streamtunes-lyrics-test" }

$otherApp = Invoke-Az @(
    "functionapp", "list",
    "--query", "[?name=='$otherAppName'].name | [0]",
    "--output", "tsv") -AllowFailure

$otherAppExists = -not [string]::IsNullOrWhiteSpace(($otherApp | Out-String).Trim())

Write-Host ("Sibling app   : {0} ({1})" -f $otherAppName, $(if ($otherAppExists) { "exists - shared resources will be kept" } else { "absent" }))

# Read BEFORE the app is deleted - afterwards there is nothing left to ask.
$contentShare = Invoke-Az @(
    "functionapp", "config", "appsettings", "list",
    "--name", $FunctionAppName, "--resource-group", $ResourceGroup,
    "--query", "[?name=='WEBSITE_CONTENTSHARE'].value | [0]", "--output", "tsv") -AllowFailure

if ($PSCmdlet.ShouldProcess($FunctionAppName, "Delete Function App")) {
    Invoke-Az @(
        "functionapp", "delete",
        "--name", $FunctionAppName, "--resource-group", $ResourceGroup) -AllowFailure | Out-Null

    Write-Host "Deleted $FunctionAppName." -ForegroundColor Green
}

# App Insights shares the app's name by default.
if ($PSCmdlet.ShouldProcess($FunctionAppName, "Delete Application Insights")) {
    Invoke-Az @(
        "monitor", "app-insights", "component", "delete",
        "--app", $FunctionAppName, "--resource-group", $ResourceGroup) -AllowFailure | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($contentShare)) {
    if ($PSCmdlet.ShouldProcess($contentShare, "Delete content file share")) {
        Invoke-Az @(
            "storage", "share", "delete",
            "--name", $contentShare,
            "--connection-string", $stagingConnection,
            "--output", "none") -AllowFailure | Out-Null
    }
}

# DO NOT add azure-webjobs-hosts or azure-webjobs-secrets here.
#
# They look like this app's leftovers and are not. Every Function App pointed at a storage account
# shares them, and all of streamtunes-media-test, streamtunes-media-prod and this app use
# musicsalesstorageaccount - so deleting them during one environment's teardown reaches into the
# others. azure-webjobs-secrets holds the function keys, which means the blast radius is PRODUCTION
# losing the keys its callbacks authenticate with.
#
# AND 'lyrics-deployment' IS NOT OURS EITHER, which this comment used to claim it was.
#
# Provision-LyricsFunctionApp.ps1 sets $deploymentContainer = "lyrics-deployment" as a bare literal
# and passes it as --deployment-storage-container-name for whichever environment is being
# provisioned, against a storage account both environments share. So both Function apps keep their
# Flex one-deploy package in this one container.
#
# It is the worst of the shared resources to delete, not the least. Flex Consumption scales to zero
# and fetches that package from here on every cold start, so the surviving environment does not
# degrade at the next deploy - it stops serving as soon as it has no warm instance, and recovery is
# a full republish rather than something that re-downloads itself. The container name is also
# briefly unusable after deletion, so re-provisioning immediately can fail too.
#
# Unlike the shares below, this ran unconditionally: no switch was needed to trigger it.
if ($otherAppExists) {
    Write-Warning ("SKIPPED the 'lyrics-deployment' container. '$otherAppName' ($otherEnvironment) " +
        "stores its Flex deployment package in it too, and deleting it would stop that app serving " +
        "on its next cold start.")
}
else {
    foreach ($container in @("lyrics-deployment")) {
        if ($PSCmdlet.ShouldProcess($container, "Delete container")) {
            Invoke-Az @(
                "storage", "container", "delete",
                "--name", $container,
                "--connection-string", $stagingConnection,
                "--output", "none") -AllowFailure | Out-Null
        }
    }
}

if ($IncludeTaskHub) {
    # See the header. None of these names mention the Function app, and nothing else will ever
    # remove them.
    $hubTables = @("$($taskHubName)History", "$($taskHubName)Instances")
    $hubQueues = @("$taskHubName-workitems") + (0..3 | ForEach-Object { "$taskHubName-control-{0:00}" -f $_ })
    $hubContainer = "$($taskHubName.ToLowerInvariant())-leases"

    foreach ($table in $hubTables) {
        if ($PSCmdlet.ShouldProcess($table, "Delete task hub table")) {
            Invoke-Az @(
                "storage", "table", "delete",
                "--name", $table, "--connection-string", $stagingConnection, "--output", "none") -AllowFailure | Out-Null
        }
    }

    foreach ($queue in $hubQueues) {
        if ($PSCmdlet.ShouldProcess($queue, "Delete task hub queue")) {
            Invoke-Az @(
                "storage", "queue", "delete",
                "--name", $queue, "--connection-string", $stagingConnection, "--output", "none") -AllowFailure | Out-Null
        }
    }

    if ($PSCmdlet.ShouldProcess($hubContainer, "Delete task hub lease container")) {
        Invoke-Az @(
            "storage", "container", "delete",
            "--name", $hubContainer, "--connection-string", $stagingConnection, "--output", "none") -AllowFailure | Out-Null
    }

    Write-Host "Removed the '$taskHubName' task hub's tables, queues and lease container." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Warning ("The '$taskHubName' task hub was left in place, so any in-flight alignment is " +
        "preserved. Re-provisioning will ADOPT it, history and all. Pass -IncludeTaskHub to start clean.")
}

if ($IncludeSharedFiles) {
    # THE SHARES ARE NOT THIS ENVIRONMENT'S. Provision-LyricsFunctionApp.ps1 names them with literals
    # - "lyrics-models" and "lyrics-tools", no environment suffix - and Test and Production resolve
    # StagingStorageConnectionString to the same account, so both apps mount the same two shares.
    #
    # Sharing them is deliberate and worth keeping: the weights are immutable and content-addressed by
    # torch's and Demucs's own cache layouts, so Production inherits whatever Test downloads and no
    # copy step exists to be forgotten at go-live. The task hub is suffixed precisely because it is
    # the opposite case - shared orchestration state means two environments stealing each other's runs.
    #
    # But it makes THIS switch reach outside the environment being torn down. Deleting the shares
    # while the other app is running pulls /mnt/models and /mnt/tools out from under it mid-alignment:
    # ffmpeg vanishes and FFMPEG_BINARY points at nothing. Same shape as the azure-webjobs-secrets
    # hazard above, one layer out - and recoverable rather than catastrophic, since Stage-LyricsMounts
    # re-uploads ffmpeg and the weights re-download on next use.
    if ($otherAppExists) {
        Write-Warning ("SKIPPED the shared files. '$otherAppName' ($otherEnvironment) still exists and mounts " +
            "the same 'lyrics-models' and 'lyrics-tools' shares, so deleting them here would break it. " +
            "Delete them by hand if that is genuinely what you want.")
    }
    else {
        foreach ($share in @("lyrics-models", "lyrics-tools")) {
            if ($PSCmdlet.ShouldProcess($share, "Delete Azure Files share")) {
                Invoke-Az @(
                    "storage", "share", "delete",
                    "--name", $share, "--connection-string", $stagingConnection, "--output", "none") -AllowFailure | Out-Null
            }
        }

        Write-Warning "Model weights and the ffmpeg build are gone; re-provisioning needs them uploaded again."
    }
}

# Never touched, in either script: the storage accounts themselves, the audio pipeline's queues, the
# staging container, and the account-wide lifecycle policy. Test and Production share that account.
Write-Host ""
Write-Host "Storage accounts, the audio pipeline's queues and the staging container were not touched." -ForegroundColor Cyan
