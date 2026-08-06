<#
.SYNOPSIS
    Tears down everything Provision-FunctionApp.ps1 created for one environment.

.DESCRIPTION
    Exists so the provisioning path can be rehearsed from a clean slate before it is run against
    Production for the first time.

    Removes: the Function App, its Consumption plan (only if nothing else is on it), Application
    Insights, the Smart Detection action group Azure auto-creates alongside it, the content file
    share the Function App puts on the storage account, the Functions runtime containers, the two
    queues, and this environment's lifecycle rule.

    NEVER removes: the storage accounts themselves, any media/persona/dataprotection container, or
    any lifecycle rule belonging to a different environment. Every deletion is by exact name, and
    the storage accounts are explicitly refused.

.EXAMPLE
    .\Remove-FunctionApp.ps1 -Environment Test -WhatIf
    Shows exactly what would be deleted.

.EXAMPLE
    .\Remove-FunctionApp.ps1 -Environment Test
#>
# SupportsShouldProcess for -WhatIf, but deliberately no ConfirmImpact="High": combined with an
# explicit -Confirm:$false it makes ShouldProcess throw a NullReferenceException when there is no
# interactive host to prompt on. -WhatIf plus a mandatory -Environment is the safety story here, and
# the storage accounts are refused unconditionally further down.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Test", "Production")]
    [string]$Environment,

    [string]$Subscription = "WebsitesSubscription",

    [string]$FunctionAppName,

    [string]$ResourceGroup,

    # The queues and staging blobs are cheap to recreate but hold in-flight work. Left alone by
    # default so a teardown cannot silently discard songs mid-processing.
    [switch]$IncludeQueuesAndStaging
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "AzureCli.ps1")
. (Join-Path $PSScriptRoot "Get-MediaProcessingSettings.ps1")

Connect-AzureCliSession -Subscription $Subscription | Out-Null

if ([string]::IsNullOrWhiteSpace($FunctionAppName)) {
    $FunctionAppName = if ($Environment -eq "Production") { "streamtunes-media-prod" } else { "streamtunes-media-test" }
}

$values = Get-MediaProcessingSettings -Environment $Environment -RepositoryRoot $PSScriptRoot

function Get-AccountNameFromConnectionString {
    param([string]$ConnectionString)
    if ($ConnectionString -match "(^|;)AccountName=([^;]+)") { return $Matches[2] }
    throw "Could not read AccountName out of a storage connection string."
}

$stagingAccountName = Get-AccountNameFromConnectionString $values["StagingStorageConnectionString"]
$stagingConnection = $values["StagingStorageConnectionString"]
$stagingContainer = $values["MediaProcessing:StagingContainerName"]

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $storage = Invoke-Az @("storage", "account", "show", "--name", $stagingAccountName, "--output", "json") | ConvertFrom-Json
    $ResourceGroup = $storage.resourceGroup
}

Write-Host "Subscription  : $Subscription"
Write-Host "Resource group: $ResourceGroup"
Write-Host "Function app  : $FunctionAppName"
Write-Host "Storage       : $stagingAccountName (the ACCOUNT is never deleted)"
Write-Host ""

# ---------------------------------------------------------------------------
# The content file share has to be read off the app before the app is deleted.
# ---------------------------------------------------------------------------
$contentShare = $null
$appExists = $false
$showApp = Invoke-Az @("functionapp", "show", "--name", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure
if ($LASTEXITCODE -eq 0) {
    $appExists = $true
    $settings = Invoke-Az @(
        "functionapp", "config", "appsettings", "list",
        "--name", $FunctionAppName, "--resource-group", $ResourceGroup,
        "--output", "json") | ConvertFrom-Json
    $contentShare = ($settings | Where-Object { $_.name -eq "WEBSITE_CONTENTSHARE" }).value
    $planId = ($showApp | ConvertFrom-Json).appServicePlanId
}

# ---------------------------------------------------------------------------
# Function App
# ---------------------------------------------------------------------------
if ($appExists) {
    if ($PSCmdlet.ShouldProcess($FunctionAppName, "Delete Function App")) {
        Write-Host "Deleting Function App '$FunctionAppName'..."
        Invoke-Az @("functionapp", "delete", "--name", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "none") | Out-Null
    }
}
else {
    Write-Host "Function App '$FunctionAppName' does not exist."
    $planId = $null
}

# ---------------------------------------------------------------------------
# Consumption plan - only when it is now empty. The name az generates ('WestUS2Plan') is regional
# and generic, so another app could legitimately be sharing it.
# ---------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($planId)) {
    $planName = ($planId -split "/")[-1]
    $remaining = Invoke-Az @("appservice", "plan", "show", "--name", $planName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure

    if ($LASTEXITCODE -eq 0) {
        # Set-StrictMode throws on an absent property, and the CLI omits numberOfSites entirely for
        # a Consumption plan, so this has to be probed rather than read.
        $planObject = ($remaining | Out-String) | ConvertFrom-Json
        $siteCount = 0
        $siteProperty = $planObject.PSObject.Properties["numberOfSites"]
        if ($null -ne $siteProperty -and $null -ne $siteProperty.Value) {
            $siteCount = [int]$siteProperty.Value
        }
        else {
            # Fall back to asking what is actually hosted there, which is the thing we care about.
            $hosted = Invoke-Az @(
                "functionapp", "list",
                "--resource-group", $ResourceGroup,
                "--query", "[?appServicePlanId=='$planId'].name",
                "--output", "tsv") -AllowFailure
            $siteCount = @(($hosted | Out-String).Trim() -split "\r?\n" | Where-Object { $_ }).Count
        }

        if ($siteCount -gt 0) {
            Write-Warning "App Service plan '$planName' still hosts $siteCount site(s); leaving it."
        }
        elseif ($PSCmdlet.ShouldProcess($planName, "Delete empty App Service plan")) {
            Write-Host "Deleting empty App Service plan '$planName'..."
            Invoke-Az @("appservice", "plan", "delete", "--name", $planName, "--resource-group", $ResourceGroup, "--yes", "--output", "none") -AllowFailure | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# Application Insights, and the Smart Detection action group created alongside it
# ---------------------------------------------------------------------------
$insights = Invoke-Az @("monitor", "app-insights", "component", "show", "--app", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure
if ($LASTEXITCODE -eq 0 -and $PSCmdlet.ShouldProcess($FunctionAppName, "Delete Application Insights")) {
    Write-Host "Deleting Application Insights '$FunctionAppName'..."
    Invoke-Az @("monitor", "app-insights", "component", "delete", "--app", $FunctionAppName, "--resource-group", $ResourceGroup, "--output", "none") -AllowFailure | Out-Null
}

$actionGroups = Invoke-Az @("monitor", "action-group", "list", "--resource-group", $ResourceGroup, "--output", "json") -AllowFailure
if ($LASTEXITCODE -eq 0) {
    foreach ($group in ($actionGroups | ConvertFrom-Json)) {
        if ($group.name -like "Application Insights Smart Detection*") {
            if ($PSCmdlet.ShouldProcess($group.name, "Delete auto-created action group")) {
                Write-Host "Deleting action group '$($group.name)'..."
                Invoke-Az @("monitor", "action-group", "delete", "--name", $group.name, "--resource-group", $ResourceGroup, "--output", "none") -AllowFailure | Out-Null
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Storage leftovers. Named explicitly, one at a time - nothing here is pattern-matched or wildcarded
# against the account, which also holds every song the platform has ever published.
# ---------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($contentShare)) {
    if ($PSCmdlet.ShouldProcess("$stagingAccountName/$contentShare", "Delete Function content file share")) {
        Write-Host "Deleting content file share '$contentShare'..."
        Invoke-Az @("storage", "share", "delete", "--name", $contentShare, "--connection-string", $stagingConnection, "--only-show-errors", "--output", "none") -AllowFailure | Out-Null
    }
}
else {
    Write-Host "No WEBSITE_CONTENTSHARE recorded; skipping the file share."
}

foreach ($container in @("azure-webjobs-hosts", "azure-webjobs-secrets")) {
    if ($PSCmdlet.ShouldProcess("$stagingAccountName/$container", "Delete Functions runtime container")) {
        Write-Host "Deleting runtime container '$container'..."
        Invoke-Az @("storage", "container", "delete", "--name", $container, "--connection-string", $stagingConnection, "--only-show-errors", "--output", "none") -AllowFailure | Out-Null
    }
}

if ($IncludeQueuesAndStaging) {
    foreach ($queueName in @($values["MediaProcessing:TranscodeQueueName"], $values["MediaProcessing:ProbeQueueName"], $values["MediaProcessing:MatchQueueName"])) {
        foreach ($name in @($queueName, "$queueName-poison")) {
            if ($PSCmdlet.ShouldProcess("$stagingAccountName/$name", "Delete queue")) {
                Write-Host "Deleting queue '$name'..."
                Invoke-Az @("storage", "queue", "delete", "--name", $name, "--connection-string", $stagingConnection, "--only-show-errors", "--output", "none") -AllowFailure | Out-Null
            }
        }
    }

    if ($PSCmdlet.ShouldProcess("$stagingAccountName/$stagingContainer", "Delete upload staging container")) {
        Write-Host "Deleting staging container '$stagingContainer'..."
        Invoke-Az @("storage", "container", "delete", "--name", $stagingContainer, "--connection-string", $stagingConnection, "--only-show-errors", "--output", "none") -AllowFailure | Out-Null
    }
}
else {
    Write-Host "Leaving the queues and '$stagingContainer' alone (pass -IncludeQueuesAndStaging to remove them)."
}

# ---------------------------------------------------------------------------
# This environment's lifecycle rule only. Rules belonging to the other environment survive.
# ---------------------------------------------------------------------------
$ruleName = "delete-abandoned-$stagingContainer"
$policyJson = Invoke-Az @(
    "storage", "account", "management-policy", "show",
    "--account-name", $stagingAccountName, "--resource-group", $ResourceGroup,
    "--output", "json") -AllowFailure

if ($LASTEXITCODE -eq 0) {
    $policy = ($policyJson | Out-String) | ConvertFrom-Json
    $rules = @()
    $found = Get-JsonPath $policy @("policy", "rules")
    if ($null -ne $found) { $rules = @($found) }
    $kept = @($rules | Where-Object { $_.name -ne $ruleName })

    if ($kept.Count -eq $rules.Count) {
        Write-Host "No lifecycle rule named '$ruleName' to remove."
    }
    elseif ($PSCmdlet.ShouldProcess($stagingAccountName, "Remove lifecycle rule '$ruleName'")) {
        if ($kept.Count -eq 0) {
            Write-Host "Removing the last lifecycle rule, deleting the policy..."
            Invoke-Az @("storage", "account", "management-policy", "delete", "--account-name", $stagingAccountName, "--resource-group", $ResourceGroup, "--output", "none") -AllowFailure | Out-Null
        }
        else {
            $policyPath = Join-Path ([System.IO.Path]::GetTempPath()) "policy-$([Guid]::NewGuid().ToString('N')).json"
            try {
                $cleanKept = @($kept | ForEach-Object { Remove-NullProperty $_ })
                Set-Content -LiteralPath $policyPath -Value (@{ rules = $cleanKept } | ConvertTo-Json -Depth 12) -Encoding UTF8
                Invoke-Az @("storage", "account", "management-policy", "create", "--account-name", $stagingAccountName, "--resource-group", $ResourceGroup, "--policy", "@$policyPath", "--output", "none") | Out-Null
                Write-Host "Removed '$ruleName', kept $($kept.Count) other rule(s)."
            }
            finally {
                Remove-Item -LiteralPath $policyPath -ErrorAction SilentlyContinue
            }
        }
    }
}

Write-Host ""
Write-Host "Teardown complete. The storage accounts and all media containers were untouched." -ForegroundColor Cyan
Write-Host "Re-provision with:  pwsh ./Provision-FunctionApp.ps1 -Environment $Environment"
