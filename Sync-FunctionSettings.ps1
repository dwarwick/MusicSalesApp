<#
.SYNOPSIS
    Writes MusicSalesApp.Functions/local.settings.json from the web app's appsettings file.

.DESCRIPTION
    The Function needs the same storage connection strings the web app already has. Rather than
    keeping a second copy of those secrets in sync by hand, this reads
    MusicSalesApp/appsettings.{Environment}.json and generates local.settings.json from it.

    Both files are gitignored, so nothing secret is ever committed.

    LOCAL ONLY. In Azure there are no settings files - each of the two Function Apps carries its own
    Application Settings, and that is the per-environment mechanism. Use Provision-FunctionApp.ps1
    to create and configure those, or -ShowAzureCli here to print the equivalent az command.

    WHY local.settings.json rather than appsettings.{Environment}.json in the Functions project:
    the queue trigger bindings resolve their queue names and their storage connection through the
    Functions *host*, before the worker's IConfiguration exists. A per-environment JSON file loaded
    by the worker could not supply them. local.settings.json is the one file the host reads.

    THERE ARE TWO FUNCTION APPS. -AppKind picks which one's settings to write: the .NET
    audio-processing app, or the Python lyrics-alignment app. They share both storage accounts and
    the callback secret but need different setting sets, so each has its own Get-*Settings helper
    and this dispatches between them.

    Unlike those helpers, -AppKind is safe to put on THIS script: their results also feed the
    drift-check baseline in the provisioning scripts, where a merged set would report an app's
    settings as permanently missing. This one only writes a file.

.EXAMPLE
    .\Sync-FunctionSettings.ps1 -Environment Test
    Points the local audio-processing Function at davidtest.dev and its containers.

.EXAMPLE
    .\Sync-FunctionSettings.ps1 -Environment Test -AppKind Lyrics
    Points the local lyrics-alignment Function at davidtest.dev.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet("Development", "Test", "Production")]
    [string]$Environment = "Development",

    # Which of the two Function apps to write settings for.
    [ValidateSet("Media", "Lyrics")]
    [string]$AppKind = "Media",

    [string]$SettingsPath,

    # Defaulted from -AppKind below, because the default depends on it.
    [string]$OutputPath,

    [switch]$ShowAzureCli,

    [string]$FunctionAppName
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Get-MediaProcessingSettings.ps1")
. (Join-Path $PSScriptRoot "Get-LyricsProcessingSettings.ps1")

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $projectFolder = if ($AppKind -eq "Lyrics") { "MusicSalesApp.LyricsFunctions" } else { "MusicSalesApp.Functions" }
    $OutputPath = Join-Path $PSScriptRoot (Join-Path $projectFolder "local.settings.json")
}

if ($Environment -eq "Production") {
    # local.settings.json is only ever read when running the Function on this machine, so this
    # points a dev box at the production queues and the production callback URL. Occasionally the
    # right thing to do; never something to do by accident, which is why there is no VS Code task
    # for it.
    Write-Warning ("This writes PRODUCTION queues, containers and callback URL into a LOCAL settings " +
        "file. Anything you run locally will process real creator uploads.")
}

$values = if ($AppKind -eq "Lyrics") {
    Get-LyricsProcessingSettings -Environment $Environment -SettingsPath $SettingsPath -RepositoryRoot $PSScriptRoot
}
else {
    Get-MediaProcessingSettings -Environment $Environment -SettingsPath $SettingsPath -RepositoryRoot $PSScriptRoot
}

if ($AppKind -eq "Lyrics") {
    # The mount paths in the shared settings are ANNOTATIONS FOR AZURE. On Flex Consumption the
    # Linux ffmpeg build and the model weights are on an Azure Files share, because CPU-only torch
    # plus a gigabyte of weights does not fit a zip deployment. None of that exists on a dev machine,
    # where /mnt/tools/ffmpeg is simply a path that is not there - so the generated LOCAL file points
    # at whatever is on PATH and lets torch and Demucs use their own default caches.
    #
    # Done here rather than in Get-LyricsProcessingSettings because the distinction is not the
    # environment - this file is written for local use whichever backend it points at - it is that
    # Sync writes to disk while Provision pushes to Azure.
    $values["FFMPEG_BINARY"] = "ffmpeg"
    $values.Remove("TORCH_HOME")
    $values.Remove("XDG_CACHE_HOME")
}

$document = [ordered]@{
    IsEncrypted = $false
    Values      = $values
}

if ($PSCmdlet.ShouldProcess($OutputPath, "Write Function app settings for '$Environment'")) {
    $json = $document | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
    Write-Host "Wrote $OutputPath ($Environment, $AppKind)."
    Write-Host "  Media   : $($values['MediaProcessing:MediaContainerName'])"
    Write-Host "  Staging : $($values['MediaProcessing:StagingContainerName'])"

    if ($AppKind -eq "Lyrics") {
        # No queues: this app is started over HTTP so its Durable starter can hand back the
        # orchestration's instance id, which a queue trigger has no way to do.
        Write-Host "  Task hub: $($values['LyricsTaskHubName'])"
    }
    else {
        Write-Host "  Queues  : $($values['MediaProcessing:TranscodeQueueName']), $($values['MediaProcessing:ProbeQueueName'])"
    }

    Write-Host "  Callback: $($values['CallbackBaseUrl'])"
}

if ($ShowAzureCli) {
    if ([string]::IsNullOrWhiteSpace($FunctionAppName)) {
        $slug = if ($AppKind -eq "Lyrics") { "lyrics" } else { "media" }
        $FunctionAppName = switch ($Environment) {
            "Production" { "streamtunes-$slug-prod" }
            default { "streamtunes-$slug-test" }
        }
    }

    # Quoted for pwsh. Every setting except FUNCTIONS_WORKER_RUNTIME, which the app already has.
    $pairs = $values.GetEnumerator() |
        Where-Object { $_.Key -ne "FUNCTIONS_WORKER_RUNTIME" } |
        ForEach-Object { "`"$($_.Key)=$($_.Value)`"" }

    Write-Host ""
    Write-Host "Apply the same values to Azure with:" -ForegroundColor Cyan
    Write-Host ("az functionapp config appsettings set --name $FunctionAppName " +
        "--resource-group <resource-group> --settings " + ($pairs -join " "))
}
