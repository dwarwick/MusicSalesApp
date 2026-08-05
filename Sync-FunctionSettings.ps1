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

.EXAMPLE
    .\Sync-FunctionSettings.ps1 -Environment Test
    Points the local Function at davidtest.dev and its containers.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet("Development", "Test", "Production")]
    [string]$Environment = "Development",

    [string]$SettingsPath,

    [string]$OutputPath = (Join-Path $PSScriptRoot "MusicSalesApp.Functions\local.settings.json"),

    [switch]$ShowAzureCli,

    [string]$FunctionAppName
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Get-MediaProcessingSettings.ps1")

if ($Environment -eq "Production") {
    # local.settings.json is only ever read when running the Function on this machine, so this
    # points a dev box at the production queues and the production callback URL. Occasionally the
    # right thing to do; never something to do by accident, which is why there is no VS Code task
    # for it.
    Write-Warning ("This writes PRODUCTION queues, containers and callback URL into a LOCAL settings " +
        "file. Anything you run locally will process real creator uploads.")
}

$values = Get-MediaProcessingSettings `
    -Environment $Environment `
    -SettingsPath $SettingsPath `
    -RepositoryRoot $PSScriptRoot

$document = [ordered]@{
    IsEncrypted = $false
    Values      = $values
}

if ($PSCmdlet.ShouldProcess($OutputPath, "Write Function app settings for '$Environment'")) {
    $json = $document | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
    Write-Host "Wrote $OutputPath ($Environment)."
    Write-Host "  Media   : $($values['MediaProcessing:MediaContainerName'])"
    Write-Host "  Staging : $($values['MediaProcessing:StagingContainerName'])"
    Write-Host "  Queues  : $($values['MediaProcessing:TranscodeQueueName']), $($values['MediaProcessing:ProbeQueueName'])"
    Write-Host "  Callback: $($values['CallbackBaseUrl'])"
}

if ($ShowAzureCli) {
    if ([string]::IsNullOrWhiteSpace($FunctionAppName)) {
        $FunctionAppName = switch ($Environment) {
            "Production" { "streamtunes-media-prod" }
            default { "streamtunes-media-test" }
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
