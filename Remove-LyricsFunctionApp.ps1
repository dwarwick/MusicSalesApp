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
    $show = Invoke-Az @(
        "functionapp", "show",
        "--name", $FunctionAppName, "--output", "json") -AllowFailure

    if ($LASTEXITCODE -ne 0) {
        Write-Host "$FunctionAppName does not exist in subscription '$($account.name)'. Nothing to remove." -ForegroundColor Yellow
        return
    }

    $ResourceGroup = (Get-JsonPath ($show | ConvertFrom-Json) @("resourceGroup"))
}

Write-Host "Function app  : $FunctionAppName"
Write-Host "Resource group: $ResourceGroup"
Write-Host "Task hub      : $taskHubName"

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

# Functions runtime bookkeeping, and the Flex deployment container.
foreach ($container in @("azure-webjobs-hosts", "azure-webjobs-secrets", "lyrics-deployment")) {
    if ($PSCmdlet.ShouldProcess($container, "Delete container")) {
        Invoke-Az @(
            "storage", "container", "delete",
            "--name", $container,
            "--connection-string", $stagingConnection,
            "--output", "none") -AllowFailure | Out-Null
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
    foreach ($share in @("lyrics-models", "lyrics-tools")) {
        if ($PSCmdlet.ShouldProcess($share, "Delete Azure Files share")) {
            Invoke-Az @(
                "storage", "share", "delete",
                "--name", $share, "--connection-string", $stagingConnection, "--output", "none") -AllowFailure | Out-Null
        }
    }

    Write-Warning "Model weights and the ffmpeg build are gone; re-provisioning needs them uploaded again."
}

# Never touched, in either script: the storage accounts themselves, the audio pipeline's queues, the
# staging container, and the account-wide lifecycle policy. Test and Production share that account.
Write-Host ""
Write-Host "Storage accounts, the audio pipeline's queues and the staging container were not touched." -ForegroundColor Cyan
