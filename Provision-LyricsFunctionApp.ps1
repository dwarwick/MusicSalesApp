<#
.SYNOPSIS
    Creates or verifies the lyrics-alignment Function app (Python, Linux, Flex Consumption).

.DESCRIPTION
    The sibling of Provision-FunctionApp.ps1, for the SECOND Function app. Idempotent: safe to
    re-run, and on a run that does not create the app it only reports drift rather than pushing
    settings over what is already there.

    A separate script rather than a switch on the existing one. They share the sign-in, the
    provider registration, the App Insights step and the drift-check philosophy - all of which come
    from AzureCli.ps1 or are a handful of lines - but the part that actually creates the app has
    nothing in common: Flex Consumption takes a different set of CLI arguments entirely, must NOT
    have WEBSITE_RUN_FROM_PACKAGE set, needs a deployment storage container, and needs an Azure
    Files mount that the Windows app has no use for. Threading all of that through one script as
    conditionals would make both halves harder to read than either is alone.

.EXAMPLE
    pwsh ./Provision-LyricsFunctionApp.ps1 -Environment Test
    pwsh ./Provision-LyricsFunctionApp.ps1 -Environment Test -WhatIf   # drift check only
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Test", "Production")]
    [string]$Environment,

    # Set explicitly rather than inherited from whatever `az account set` was last run. The selected
    # subscription is ambient global state shared with every other project on this machine, and it
    # silently decides where production resources get created.
    [string]$Subscription = "WebsitesSubscription",

    [string]$FunctionAppName,

    [string]$ResourceGroup,

    [string]$Location,

    [string]$AppInsightsName,

    # 4096 MB is the top Flex tier and allocates roughly two cores. Because billing is per
    # GB-second, it costs about the same per song as 2048 MB while running roughly twice as fast -
    # so there is no reason to pick less.
    [ValidateSet(2048, 4096)]
    [int]$InstanceMemoryMb = 4096,

    # Each instance holds one song's audio, its vocal stem and a model in memory. The cap is a cost
    # guard rather than a capacity one: nothing here is latency-sensitive enough to justify a large
    # fleet of 4 GB instances.
    [int]$MaximumInstanceCount = 10,

    [string]$PythonVersion = "3.11",

    [switch]$SkipAppInsights,

    # Settings are pushed automatically only when this run creates the app. Once an environment
    # exists the portal is authoritative; this forces the appsettings file back over it.
    [switch]$ApplySettings
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

function Get-AccountNameFromConnectionString {
    param([string]$ConnectionString)
    if ($ConnectionString -match "(^|;)AccountName=([^;]+)") { return $Matches[2] }
    throw "Could not read AccountName out of a storage connection string."
}

$stagingAccountName = Get-AccountNameFromConnectionString $values["StagingStorageConnectionString"]
$mediaAccountName = Get-AccountNameFromConnectionString $values["MediaStorageConnectionString"]

$deploymentContainer = "lyrics-deployment"
$modelShare = "lyrics-models"
$toolShare = "lyrics-tools"

Write-Host "Function app  : $FunctionAppName (Python $PythonVersion, Flex Consumption, ${InstanceMemoryMb}MB)"
Write-Host "Task hub      : $($values['LyricsTaskHubName'])"
Write-Host "Task hub/stage: $stagingAccountName (must be Standard general-purpose v2)"
Write-Host "Media         : $mediaAccountName (premium; read-only from this app)"

# ---------------------------------------------------------------------------
# Storage
# ---------------------------------------------------------------------------
function Get-StorageAccountOrExplain {
    param([string]$Name, [string]$Role)

    $result = Invoke-Az @("storage", "account", "show", "--name", $Name, "--output", "json") -AllowFailure
    if ($LASTEXITCODE -eq 0) {
        return $result | ConvertFrom-Json
    }

    throw ("Storage account '$Name' ($Role) was not found in subscription " +
        "'$($account.name)'.`n`nIf StreamTunes' storage lives elsewhere, re-run with " +
        "-Subscription `"<name>`". Available:`n`n" +
        ((Invoke-Az @("account", "list", "--query", "[].name", "--output", "tsv") | ForEach-Object { "    $_" }) -join "`n"))
}

$stagingAccount = Get-StorageAccountOrExplain -Name $stagingAccountName -Role "Durable task hub and staging"

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) { $ResourceGroup = $stagingAccount.resourceGroup }
if ([string]::IsNullOrWhiteSpace($Location)) { $Location = $stagingAccount.location }

Write-Host "Resource group: $ResourceGroup"
Write-Host "Location      : $Location"

# The Durable task hub is queues and tables. Same constraint that forced two storage accounts in the
# first place, and worth failing on here with a legible message rather than at runtime with an
# obscure binding error.
if ($stagingAccount.sku.tier -ne "Standard" -or $stagingAccount.kind -ne "StorageV2") {
    throw ("'$stagingAccountName' is $($stagingAccount.kind)/$($stagingAccount.sku.tier). " +
        "The Durable task hub needs a Standard general-purpose v2 account - no premium account type " +
        "offers the Queue service.")
}

# ---------------------------------------------------------------------------
# Providers and App Insights
# ---------------------------------------------------------------------------
foreach ($provider in @("Microsoft.Web", "microsoft.insights", "microsoft.operationalinsights")) {
    if ($SkipAppInsights -and $provider -ne "Microsoft.Web") { continue }
    if ($PSCmdlet.ShouldProcess($provider, "Register resource provider")) {
        Invoke-Az @("provider", "register", "--namespace", $provider) -AllowFailure | Out-Null
    }
}

$appInsightsConnectionString = $null

if (-not $SkipAppInsights) {
    if ([string]::IsNullOrWhiteSpace($AppInsightsName)) { $AppInsightsName = $FunctionAppName }

    $existing = Invoke-Az @(
        "monitor", "app-insights", "component", "show",
        "--app", $AppInsightsName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure

    if ($LASTEXITCODE -ne 0) {
        if ($PSCmdlet.ShouldProcess($AppInsightsName, "Create Application Insights")) {
            $existing = Invoke-Az @(
                "monitor", "app-insights", "component", "create",
                "--app", $AppInsightsName,
                "--location", $Location,
                "--resource-group", $ResourceGroup,
                "--application-type", "web",
                "--output", "json")
        }
    }

    if ($existing) {
        $appInsightsConnectionString = (Get-JsonPath ($existing | ConvertFrom-Json) @("connectionString"))
    }
}

# ---------------------------------------------------------------------------
# The Function app itself
# ---------------------------------------------------------------------------
$show = Invoke-Az @(
    "functionapp", "show",
    "--name", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure

$appAlreadyExisted = ($LASTEXITCODE -eq 0)

if (-not $appAlreadyExisted) {
    # Flex needs somewhere to put the deployment payload. Unlike Consumption's
    # WEBSITE_RUN_FROM_PACKAGE this is a real container the platform manages.
    if ($PSCmdlet.ShouldProcess($deploymentContainer, "Create deployment container")) {
        Invoke-Az @(
            "storage", "container", "create",
            "--name", $deploymentContainer,
            "--account-name", $stagingAccountName,
            "--connection-string", $values["StagingStorageConnectionString"],
            "--public-access", "off",
            "--output", "none") -AllowFailure | Out-Null
    }

    # Flex Consumption. Note what is NOT here versus the C# app: no --consumption-plan-location,
    # no --os-type Windows, no --net-framework-version. Python on Azure Functions is Linux-only,
    # which is the whole reason this is a second app.
    $createArgs = @(
        "functionapp", "create",
        "--name", $FunctionAppName,
        "--resource-group", $ResourceGroup,
        "--flexconsumption-location", $Location,
        "--runtime", "python",
        "--runtime-version", $PythonVersion,
        "--instance-memory", "$InstanceMemoryMb",
        "--maximum-instance-count", "$MaximumInstanceCount",
        "--storage-account", $stagingAccountName,
        "--deployment-storage-name", $stagingAccountName,
        "--deployment-storage-container-name", $deploymentContainer,
        "--output", "json")

    if ($appInsightsConnectionString) {
        $createArgs += @("--app-insights-key", $appInsightsConnectionString)
    }

    if ($PSCmdlet.ShouldProcess($FunctionAppName, "Create Flex Consumption Function App")) {
        Invoke-Az $createArgs | Out-Null
        Write-Host "Created $FunctionAppName." -ForegroundColor Green
    }
}
else {
    Write-Host "$FunctionAppName already exists." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Settings: pushed on the run that creates the app, reported on every other run.
# ---------------------------------------------------------------------------
$shouldApplySettings = $ApplySettings -or (-not $appAlreadyExisted)

if ($shouldApplySettings) {
    $settingPairs = $values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }

    # NOTE: WEBSITE_RUN_FROM_PACKAGE is deliberately absent. Provision-FunctionApp.ps1 appends it
    # unconditionally at this exact point, and copying that line across breaks Flex - it has its own
    # one-deploy mechanism and the two do not coexist.

    if ($PSCmdlet.ShouldProcess($FunctionAppName, "Apply $($settingPairs.Count) app settings")) {
        Invoke-Az (@(
            "functionapp", "config", "appsettings", "set",
            "--name", $FunctionAppName,
            "--resource-group", $ResourceGroup,
            "--settings") + $settingPairs + @("--output", "none")) | Out-Null

        Write-Host "Applied $($settingPairs.Count) app settings." -ForegroundColor Green
    }
}
else {
    # Read-only drift check. Key names only, never values - this output ends up in terminals and
    # screenshots.
    $current = Invoke-Az @(
        "functionapp", "config", "appsettings", "list",
        "--name", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure

    if ($LASTEXITCODE -eq 0) {
        $currentNames = ($current | ConvertFrom-Json) | ForEach-Object { $_.name }
        $missing = $values.Keys | Where-Object { $currentNames -notcontains $_ }

        if ($missing) {
            Write-Warning ("Settings present in appsettings.$Environment.json but missing on the app: " +
                ($missing -join ", "))
            Write-Warning "Add them in the portal, or re-run with -ApplySettings."
        }
        else {
            Write-Host "No settings drift." -ForegroundColor Green
        }
    }
}

# ---------------------------------------------------------------------------
# Azure Files: the Linux ffmpeg and the model weights.
# ---------------------------------------------------------------------------
# The single biggest deployment risk in this app. CPU-only torch is already several hundred
# megabytes installed; htdemucs plus the MMS_FA bundle is well over a gigabyte on top, and Flex is
# zip-deploy. Mounting them is the difference between a package that deploys and one that does not.
#
# VERIFY Azure Files mount support on Flex against current docs before relying on this. If it is
# unavailable the fallback is imageio-ffmpeg plus baked-in weights, which collides head-on with the
# size problem and would change the hosting decision rather than just this script.
foreach ($share in @($modelShare, $toolShare)) {
    if ($PSCmdlet.ShouldProcess($share, "Create Azure Files share")) {
        Invoke-Az @(
            "storage", "share", "create",
            "--name", $share,
            "--connection-string", $values["StagingStorageConnectionString"],
            "--quota", "10",
            "--output", "none") -AllowFailure | Out-Null
    }
}

$stagingKey = (Invoke-Az @(
    "storage", "account", "keys", "list",
    "--account-name", $stagingAccountName,
    "--query", "[0].value", "--output", "tsv") -AllowFailure)

if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($stagingKey)) {
    foreach ($mount in @(
            @{ Id = "models"; Share = $modelShare; Path = "/mnt/models" },
            @{ Id = "tools"; Share = $toolShare; Path = "/mnt/tools" })) {

        if ($PSCmdlet.ShouldProcess($mount.Path, "Mount Azure Files share")) {
            Invoke-Az @(
                "functionapp", "config", "storage-account", "add",
                "--name", $FunctionAppName,
                "--resource-group", $ResourceGroup,
                "--custom-id", $mount.Id,
                "--storage-type", "AzureFiles",
                "--account-name", $stagingAccountName,
                "--share-name", $mount.Share,
                "--access-key", $stagingKey,
                "--mount-path", $mount.Path,
                "--output", "none") -AllowFailure | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# The function key the web app needs.
# ---------------------------------------------------------------------------
# This is the one value that has to travel back by hand. It authorises the WEB APP to call this
# Function app, which is the opposite direction from MediaProcessingApiKey - so it is a separate
# secret and lives in the web app's own settings, not here.
if (-not $WhatIfPreference) {
    $key = Invoke-Az @(
        "functionapp", "keys", "list",
        "--name", $FunctionAppName, "--resource-group", $ResourceGroup,
        "--query", "functionKeys.default", "--output", "tsv") -AllowFailure

    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($key)) {
        Write-Host ""
        Write-Host "Add these to MusicSalesApp/appsettings.$Environment.json:" -ForegroundColor Cyan
        Write-Host "  `"LyricsFunctions`": {"
        Write-Host "    `"BaseUrl`": `"https://$FunctionAppName.azurewebsites.net`","
        Write-Host "    `"FunctionKey`": `"$key`""
        Write-Host "  }"
        Write-Host ""
        Write-Host "Until both are set the web app reports lyric timing as unavailable and everything else carries on." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Next: pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName $FunctionAppName -Runtime python" -ForegroundColor Cyan
Write-Host "Upload a Linux ffmpeg static build to the '$toolShare' share, and the Demucs/torchaudio weights to '$modelShare'." -ForegroundColor Cyan
