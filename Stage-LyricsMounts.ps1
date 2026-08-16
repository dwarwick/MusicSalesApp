<#
.SYNOPSIS
    Puts the Linux ffmpeg build onto the lyrics Function app's Azure Files mount.

.DESCRIPTION
    Provisioning creates the shares and mounts them; this fills one of them. Separate from
    Provision-LyricsFunctionApp.ps1 because it moves ~150 MB over the wire and there is no reason to
    repeat that on every idempotent provisioning re-run.

    WHY THIS EXISTS AT ALL, rather than the binaries living in the deployment package: Flex
    Consumption is zip-deploy, and CPU-only torch plus its dependencies already fill most of what a
    package can carry. ffmpeg and ffprobe are 76 MB each on top.

    WHAT ABOUT THE MODEL WEIGHTS? Deliberately not here, and not needed. TORCH_HOME and
    XDG_CACHE_HOME point at /mnt/models, so torchaudio and Demucs write their checkpoints onto the
    mount the first time they load a model - roughly 1.3 GB, about 40 seconds - and every instance
    afterwards reads them from there. Reproducing their cache layouts by hand would be a guess at
    something only they control. The first alignment after a fresh mount pays that cost once; use
    -WarmModels to pay it up front instead.

.EXAMPLE
    pwsh ./Stage-LyricsMounts.ps1 -Environment Test
    pwsh ./Stage-LyricsMounts.ps1 -Environment Test -Force   # re-upload even if present
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Test", "Production")]
    [string]$Environment,

    [string]$Subscription = "WebsitesSubscription",

    # Pinned rather than "release": a moving target means the binary under the app can change without
    # anything in this repository changing, and an alignment pipeline is not somewhere to discover
    # that ffmpeg's silencedetect output format shifted.
    [string]$FfmpegUrl = "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz",

    [string]$ToolShare = "lyrics-tools",

    # Skips the upload when both binaries are already on the share at a plausible size.
    [switch]$Force,

    # Also asks the deployed app to pull the model weights now, rather than letting the first
    # alignment absorb the download.
    [switch]$WarmModels
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "AzureCli.ps1")
. (Join-Path $PSScriptRoot "Get-LyricsProcessingSettings.ps1")

Connect-AzureCliSession -Subscription $Subscription | Out-Null

$values = Get-LyricsProcessingSettings -Environment $Environment -RepositoryRoot $PSScriptRoot
$connection = $values["StagingStorageConnectionString"]

Write-Host "Share: $ToolShare"

# ---------------------------------------------------------------------------
# Already there?
# ---------------------------------------------------------------------------
$existing = @{}
$listed = Invoke-Az @(
    "storage", "file", "list",
    "--share-name", $ToolShare,
    "--connection-string", $connection,
    "--output", "json") -AllowFailure

if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($listed)) {
    foreach ($entry in ($listed | ConvertFrom-Json)) {
        $existing[$entry.name] = [int64](Get-JsonPath $entry @("properties", "contentLength"))
    }
}

$needed = @("ffmpeg", "ffprobe")
# @() around the pipeline is load-bearing under Set-StrictMode: Where-Object returns $null when
# nothing matches and a bare object when one thing does, and .Count exists on neither.
$missing = @($needed | Where-Object { -not $existing.ContainsKey($_) -or $existing[$_] -lt 10MB })

if ($missing.Count -eq 0 -and -not $Force) {
    foreach ($name in $needed) {
        Write-Host ("  {0,-9} already staged ({1:N0} MB)" -f $name, ($existing[$name] / 1MB))
    }
}
else {
    if ($missing.Count -gt 0) { Write-Host "  missing: $($missing -join ', ')" }

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("lyrics-stage-" + [guid]::NewGuid().ToString("N"))
    $upload = Join-Path $work "upload"
    New-Item -ItemType Directory -Path $upload -Force | Out-Null

    try {
        $archive = Join-Path $work "ffmpeg.tar.xz"

        if ($PSCmdlet.ShouldProcess($FfmpegUrl, "Download static ffmpeg build")) {
            Write-Host "  downloading $FfmpegUrl"
            Invoke-WebRequest -Uri $FfmpegUrl -OutFile $archive -UseBasicParsing
            Write-Host ("  archive: {0:N0} MB" -f ((Get-Item $archive).Length / 1MB))

            # tar ships with Windows 10+, macOS and Linux, so no extra dependency. --strip-components
            # flattens the versioned directory the tarball wraps everything in.
            tar -xf $archive -C $work
            if ($LASTEXITCODE -ne 0) { throw "Could not extract $archive." }

            foreach ($name in $needed) {
                $found = Get-ChildItem -Path $work -Recurse -Filter $name -File | Select-Object -First 1
                if ($null -eq $found) { throw "The archive did not contain '$name'." }
                Copy-Item $found.FullName (Join-Path $upload $name) -Force
            }
        }

        if ($PSCmdlet.ShouldProcess($ToolShare, "Upload ffmpeg and ffprobe")) {
            # upload-batch, NOT `az storage file upload`. The single-file command fails with
            # ErrorCode:ParentNotFound for anything past roughly 64 MB - reproducibly, to the share
            # root and to a subdirectory alike, while a 64 MB file over identical syntax succeeds.
            # Both binaries are 76 MB. upload-batch takes a different code path and works.
            Invoke-Az @(
                "storage", "file", "upload-batch",
                "--destination", $ToolShare,
                "--source", $upload,
                "--connection-string", $connection,
                "--output", "none") | Out-Null

            Write-Host "  uploaded ffmpeg and ffprobe." -ForegroundColor Green
        }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Verify from the storage side.
# ---------------------------------------------------------------------------
$verify = Invoke-Az @(
    "storage", "file", "list",
    "--share-name", $ToolShare,
    "--connection-string", $connection,
    "--output", "json") -AllowFailure

if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($verify)) {
    Write-Host ""
    Write-Host "On the share:"
    foreach ($entry in ($verify | ConvertFrom-Json)) {
        Write-Host ("  {0,-10} {1:N1} MB" -f $entry.name, ((Get-JsonPath $entry @("properties", "contentLength")) / 1MB))
    }
}

# ---------------------------------------------------------------------------
# Model weights, on request.
# ---------------------------------------------------------------------------
if ($WarmModels) {
    Write-Host ""
    Write-Host "Model weights are pulled by the app itself, on first use, onto /mnt/models."
    Write-Host "There is nothing to upload: torchaudio and Demucs own those cache layouts, and"
    Write-Host "TORCH_HOME/XDG_CACHE_HOME already point at the mount. To pay the ~40s cost now"
    Write-Host "rather than on the first creator's song, submit one set of lyrics and let it run."
}

Write-Host ""
Write-Host "The app runs ffmpeg straight off the mount - the SMB mount presents 0777 and honours" -ForegroundColor Cyan
Write-Host "the executable bit, so no copy-to-local step is needed." -ForegroundColor Cyan
