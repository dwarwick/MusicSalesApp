<#
.SYNOPSIS
    Publishes the audio-processing Function app, resolving `func` even when PATH is stale.

.DESCRIPTION
    Azure Functions Core Tools installs itself onto the *machine* PATH, but a process that was
    already running - VS Code, and every terminal it spawns - keeps the environment it started with.
    Reload Window does not help; only a full application restart does.

    That leaves a window, right after installing Core Tools, where `func` is present on disk and on
    the machine PATH yet invisible to the shell you are sitting in. A bare `Get-Command func` check
    reports "not installed" and sends you off to reinstall a tool you already have.

    So this resolves the executable itself rather than trusting PATH, and only claims Core Tools is
    missing when no candidate exists anywhere.

.EXAMPLE
    .\Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-media-test

.EXAMPLE
    .\Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-media-test -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$FunctionAppName,

    [string]$ProjectPath,

    # Which Function app is being published. There are two, and they need different publish flags:
    # the .NET one wants --dotnet-isolated, and the Python one wants --build remote so its wheels are
    # built on a Linux worker rather than shipped from whatever machine ran this.
    [ValidateSet("dotnet", "python")]
    [string]$Runtime = "dotnet"
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

function Resolve-FuncExecutable {
    <#
        Returns a hashtable: Path, and OnPath (whether the shell could have found it itself).
        Returns $null when Core Tools genuinely is not installed.
    #>

    $onPath = Get-Command func -ErrorAction SilentlyContinue
    if ($null -ne $onPath) {
        return @{ Path = $onPath.Source; OnPath = $true }
    }

    $candidates = @(
        # winget / MSI default
        (Join-Path $env:ProgramFiles "Microsoft\Azure Functions Core Tools\func.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Azure Functions Core Tools\func.exe"),
        # winget shim directory
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\func.exe"),
        # npm -g install
        (Join-Path $env:APPDATA "npm\func.cmd"),
        (Join-Path $env:APPDATA "npm\func"),
        # Homebrew and npm on macOS/Linux. The stale-PATH problem this function exists to solve is
        # a Windows one, but the candidate list was Windows-only, so on a Mac it fell through to a
        # bare Get-Command and then reported "not installed" listing only winget and npm paths.
        "/opt/homebrew/bin/func",
        "/usr/local/bin/func",
        "/usr/bin/func"
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return @{ Path = $candidate; OnPath = $false }
        }
    }

    return $null
}

$func = Resolve-FuncExecutable

if ($null -eq $func) {
    throw ("Azure Functions Core Tools (func) is not installed.`n`n" +
        "    winget install Microsoft.Azure.FunctionsCoreTools`n`n" +
        "See https://learn.microsoft.com/azure/azure-functions/functions-run-local")
}

if (-not $func.OnPath) {
    # The distinction that matters: it IS installed, this shell just cannot see it.
    Write-Warning ("'func' is not on this session's PATH, but was found at:`n" +
        "    $($func.Path)`n" +
        "Publishing with that full path. To fix it permanently, quit VS Code completely and " +
        "reopen it - Reload Window keeps the old environment.")
}

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Functions project folder not found: $ProjectPath"
}

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = if ($Runtime -eq "python") {
        Join-Path $PSScriptRoot "MusicSalesApp.LyricsFunctions"
    }
    else {
        Join-Path $PSScriptRoot "MusicSalesApp.Functions"
    }
}

Write-Host "Publishing $ProjectPath -> $FunctionAppName ($Runtime)"
Write-Host "Using: $($func.Path)"

if (-not $PSCmdlet.ShouldProcess($FunctionAppName, "func azure functionapp publish")) {
    return
}

Push-Location $ProjectPath
try {
    $publishArgs = if ($Runtime -eq "python") {
        # Remote build: the wheels have to be built for the Linux worker, and torch is not
        # something to be cross-shipped from a Windows or macOS dev machine even if pip let you.
        @("--build", "remote")
    }
    else {
        @("--dotnet-isolated")
    }

    & $func.Path azure functionapp publish $FunctionAppName @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "func azure functionapp publish $FunctionAppName failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Published. Confirm both triggers registered with:" -ForegroundColor Cyan
Write-Host "  az functionapp function list -n $FunctionAppName -g <resource-group> --query `"[].name`" -o tsv"
