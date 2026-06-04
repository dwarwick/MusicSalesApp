[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SettingsPath = (Join-Path $PSScriptRoot "MusicSalesApp\appsettings.Test.json"),
    [string]$SourceSection = "AzureLowSpeed",
    [string]$DestinationSection = "Azure",
    [string]$SourceConnectionString,
    [string]$DestinationConnectionString,
    [string]$Prefix,
    [switch]$Overwrite,
    [switch]$Wait,
    [switch]$InstallDependencies,
    [int]$PollSeconds = 5
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

function Import-RequiredModule {
    if (-not (Get-Module -ListAvailable -Name Az.Storage)) {
        if (-not $InstallDependencies) {
            throw "Az.Storage is required. Re-run this script with -InstallDependencies, or install it with: Install-Module Az.Storage -Scope CurrentUser"
        }

        Write-Host "Installing Az.Storage for the current user..."

        if ($PSVersionTable.PSEdition -eq "Desktop") {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor
                [Net.SecurityProtocolType]::Tls12
        }

        if (Get-Command Install-PackageProvider -ErrorAction SilentlyContinue) {
            Install-PackageProvider `
                -Name NuGet `
                -MinimumVersion 2.8.5.201 `
                -Scope CurrentUser `
                -Force `
                -ErrorAction SilentlyContinue | Out-Null
        }

        Install-Module `
            -Name Az.Storage `
            -Scope CurrentUser `
            -Repository PSGallery `
            -Force `
            -AllowClobber `
            -ErrorAction Stop
    }

    Import-Module Az.Storage -ErrorAction Stop
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Read-AppSettings {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Settings file was not found: $Path"
    }

    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Get-SettingsSection {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Settings,

        [Parameter(Mandatory = $true)]
        [string]$SectionName
    )

    $section = Get-JsonPropertyValue -Object $Settings -Name $SectionName
    if ($null -eq $section) {
        throw "Settings section '$SectionName' was not found."
    }

    return $section
}

function Get-StorageConnectionStringFromSection {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Section,

        [Parameter(Mandatory = $true)]
        [string]$SectionName
    )

    $connectionString = Get-JsonPropertyValue -Object $Section -Name "StorageConnectionString"
    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        return $connectionString
    }

    $connectionString = Get-JsonPropertyValue -Object $Section -Name "StorageAccountConnectionString"
    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        return $connectionString
    }

    throw "Settings section '$SectionName' must contain StorageConnectionString or StorageAccountConnectionString."
}

function New-StorageContextFromConnectionString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConnectionString
    )

    return New-AzStorageContext -ConnectionString $ConnectionString
}

function Get-StorageAccountNameFromConnectionString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConnectionString
    )

    if ($ConnectionString -match "(^|;)AccountName=([^;]+)") {
        return $Matches[2]
    }

    return "(unknown)"
}

function Resolve-ConnectionString {
    param(
        [string]$DirectConnectionString,

        [object]$Settings,

        [Parameter(Mandatory = $true)]
        [string]$SectionName
    )

    if (-not [string]::IsNullOrWhiteSpace($DirectConnectionString)) {
        return $DirectConnectionString
    }

    if ($null -eq $Settings) {
        throw "No direct connection string was provided for '$SectionName', and settings were not loaded."
    }

    $section = Get-SettingsSection -Settings $Settings -SectionName $SectionName
    return Get-StorageConnectionStringFromSection -Section $section -SectionName $SectionName
}

function Get-AllSourceContainerPairs {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SourceContext
    )

    $containers = Get-AzStorageContainer -Context $SourceContext

    return @(
        foreach ($container in $containers) {
            [pscustomobject]@{
                SourceContainer = $container.Name
                DestinationContainer = $container.Name
            }
        }
    )
}

function Ensure-DestinationContainer {
    param(
        [Parameter(Mandatory = $true)]
        [object]$DestinationContext,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName
    )

    $container = Get-AzStorageContainer -Context $DestinationContext -Name $ContainerName -ErrorAction SilentlyContinue
    if ($null -ne $container) {
        return
    }

    New-AzStorageContainer -Context $DestinationContext -Name $ContainerName -Permission Off | Out-Null
}

function Wait-ForBlobCopy {
    param(
        [Parameter(Mandatory = $true)]
        [object]$DestinationContext,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$BlobName,

        [Parameter(Mandatory = $true)]
        [int]$DelaySeconds
    )

    while ($true) {
        $copyState = Get-AzStorageBlobCopyState `
            -Context $DestinationContext `
            -Container $ContainerName `
            -Blob $BlobName

        if ($copyState.Status -eq "Pending") {
            Start-Sleep -Seconds $DelaySeconds
            continue
        }

        if ($copyState.Status -ne "Success") {
            throw "Copy failed for '$ContainerName/$BlobName'. Status: $($copyState.Status). Description: $($copyState.StatusDescription)"
        }

        return
    }
}

function Copy-BlobContainer {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SourceContext,

        [Parameter(Mandatory = $true)]
        [object]$DestinationContext,

        [Parameter(Mandatory = $true)]
        [object]$Pair
    )

    $listParameters = @{
        Context = $SourceContext
        Container = $Pair.SourceContainer
    }

    if (-not [string]::IsNullOrWhiteSpace($Prefix)) {
        $listParameters.Prefix = $Prefix
    }

    $sourceBlobs = @(Get-AzStorageBlob @listParameters)
    if ($sourceBlobs.Count -eq 0) {
        Write-Host "No blobs found in '$($Pair.SourceContainer)'."
        return [pscustomobject]@{
            Container = $Pair.SourceContainer
            Started = 0
            Skipped = 0
            Total = 0
        }
    }

    if ($PSCmdlet.ShouldProcess($Pair.DestinationContainer, "Create destination container if missing")) {
        Ensure-DestinationContainer -DestinationContext $DestinationContext -ContainerName $Pair.DestinationContainer
    }

    $started = 0
    $skipped = 0
    $index = 0

    foreach ($blob in $sourceBlobs) {
        $index++
        $sourceName = $blob.Name
        $destinationName = $blob.Name
        $activity = "Copying $($Pair.SourceContainer) to $($Pair.DestinationContainer)"

        Write-Progress `
            -Activity $activity `
            -Status "$index of $($sourceBlobs.Count): $sourceName" `
            -PercentComplete (($index / $sourceBlobs.Count) * 100)

        $existingBlob = $null
        if (-not $WhatIfPreference) {
            $existingBlob = Get-AzStorageBlob `
                -Context $DestinationContext `
                -Container $Pair.DestinationContainer `
                -Blob $destinationName `
                -ErrorAction SilentlyContinue
        }

        if ($null -ne $existingBlob -and -not $Overwrite) {
            $skipped++
            Write-Verbose "Skipping existing blob '$($Pair.DestinationContainer)/$destinationName'. Use -Overwrite to replace it."
            continue
        }

        $copyParameters = @{
            Context = $SourceContext
            SrcContainer = $Pair.SourceContainer
            SrcBlob = $sourceName
            DestContext = $DestinationContext
            DestContainer = $Pair.DestinationContainer
            DestBlob = $destinationName
        }

        if ($Overwrite) {
            $copyParameters.Force = $true
        }

        if ($PSCmdlet.ShouldProcess("$($Pair.DestinationContainer)/$destinationName", "Copy blob")) {
            Start-AzStorageBlobCopy @copyParameters | Out-Null
            $started++

            if ($Wait) {
                Wait-ForBlobCopy `
                    -DestinationContext $DestinationContext `
                    -ContainerName $Pair.DestinationContainer `
                    -BlobName $destinationName `
                    -DelaySeconds $PollSeconds
            }
        }
    }

    Write-Progress -Activity "Copying $($Pair.SourceContainer)" -Completed

    return [pscustomobject]@{
        Container = $Pair.SourceContainer
        Started = $started
        Skipped = $skipped
        Total = $sourceBlobs.Count
    }
}

Import-RequiredModule

$needsSettings = [string]::IsNullOrWhiteSpace($SourceConnectionString) -or
    [string]::IsNullOrWhiteSpace($DestinationConnectionString)

$settings = $null
if ($needsSettings) {
    $settings = Read-AppSettings -Path $SettingsPath
}

$resolvedSourceConnectionString = Resolve-ConnectionString `
    -DirectConnectionString $SourceConnectionString `
    -Settings $settings `
    -SectionName $SourceSection

$resolvedDestinationConnectionString = Resolve-ConnectionString `
    -DirectConnectionString $DestinationConnectionString `
    -Settings $settings `
    -SectionName $DestinationSection

$sourceContext = New-StorageContextFromConnectionString -ConnectionString $resolvedSourceConnectionString
$destinationContext = New-StorageContextFromConnectionString -ConnectionString $resolvedDestinationConnectionString

$sourceAccountName = Get-StorageAccountNameFromConnectionString -ConnectionString $resolvedSourceConnectionString
$destinationAccountName = Get-StorageAccountNameFromConnectionString -ConnectionString $resolvedDestinationConnectionString

if ($sourceAccountName -eq $destinationAccountName) {
    Write-Warning "Source and destination both appear to use storage account '$sourceAccountName'."
}

$containerPairs = Get-AllSourceContainerPairs -SourceContext $sourceContext

if ($containerPairs.Count -eq 0) {
    throw "No source containers were found."
}

$sourceLabel = if ([string]::IsNullOrWhiteSpace($SourceConnectionString)) { "'$SourceSection'" } else { "source connection string" }
$destinationLabel = if ([string]::IsNullOrWhiteSpace($DestinationConnectionString)) { "'$DestinationSection'" } else { "destination connection string" }

Write-Host "Copying blobs from $sourceLabel ($sourceAccountName) to $destinationLabel ($destinationAccountName)."
if ($needsSettings) {
    Write-Host "Settings file: $SettingsPath"
}
Write-Host "Containers discovered: $($containerPairs.Count)"
if (-not [string]::IsNullOrWhiteSpace($Prefix)) {
    Write-Host "Prefix filter: $Prefix"
}

$results = foreach ($pair in $containerPairs) {
    Write-Host "Container: $($pair.SourceContainer) -> $($pair.DestinationContainer)"

    Copy-BlobContainer `
        -SourceContext $sourceContext `
        -DestinationContext $destinationContext `
        -Pair $pair
}

$results | Format-Table -AutoSize
