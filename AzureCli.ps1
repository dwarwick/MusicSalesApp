<#
.SYNOPSIS
    Shared Azure CLI helpers: Invoke-Az and Connect-AzureCliSession.

.DESCRIPTION
    Dot-source this from a provisioning or teardown script:

        . "$PSScriptRoot\AzureCli.ps1"
        Connect-AzureCliSession -Subscription "WebsitesSubscription"

    Exists because the same two problems bit repeatedly while standing this up: an expired token
    that only surfaced as a wall of AADSTS trace IDs, and the CLI stopping to ask whether it may
    install an extension while its output was captured, so the prompt was invisible and the script
    appeared to hang.
#>

Set-StrictMode -Version 3.0

# 'az monitor app-insights' lives in an installable extension, and by default the CLI stops to ask
# "Do you want to install it now? (Y/n)". Scripts here capture stdout, so that prompt would never be
# displayed and the script would hang with no explanation. Process-scoped rather than
# 'az config set', which would change this machine's global CLI behaviour for every other project.
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = "yes_without_prompt"

$script:ArmLoginArgs = @("login", "--scope", "https://management.core.windows.net//.default")

function Test-AzAuthenticationFailure {
    param([string]$Output)

    # Recognising the CLI's auth errors by text is fragile, but the raw dump buries the one line
    # that matters under trace and correlation IDs.
    return $Output -match "AADSTS" `
        -or $Output -match "Interactive authentication is needed" `
        -or $Output -match "Please run: az login" `
        -or $Output -match "refresh token has expired" `
        -or $Output -match "az login --scope" `
        -or $Output -match "Please run 'az login'"
}

function Invoke-Az {
    <#
        Runs the CLI and captures its output. Auth failures are always fatal, even under
        -AllowFailure: otherwise an expired token is indistinguishable from "this resource does not
        exist", and a provisioning script would cheerfully recreate things that already exist.
    #>
    param(
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [switch]$AllowFailure
    )

    Write-Verbose ("az " + ($Arguments -join " "))
    $output = & az @Arguments 2>&1
    $joined = ($output | Out-String)

    if ($LASTEXITCODE -ne 0) {
        if (Test-AzAuthenticationFailure $joined) {
            throw ("Your Azure CLI sign-in expired mid-run. Re-run this script; it will sign you in.")
        }

        if (-not $AllowFailure) {
            throw "az $($Arguments -join ' ') failed:`n$joined"
        }
    }

    return $output
}

function Remove-NullProperty {
    <#
        Recursively drops properties whose value is null.

        Needed whenever a document read back from `az ... --output json` is edited and fed to
        another az command. The CLI expands every optional field it knows about and returns them as
        explicit nulls - a storage management policy rule comes back carrying tierToArchive,
        daysAfterLastAccessTimeGreaterThan and a dozen others - and sending those back can be
        rejected as invalid. Stripping them leaves the document semantically identical.
    #>
    param($InputObject)

    # Deliberately not a pipeline function. With ValueFromPipeline + process{}, returning an array
    # unrolls it into the caller's assignment, so a single-element array like blobTypes=["blockBlob"]
    # silently collapses to the bare string and serialises as {"Length": 9} instead of an array.

    if ($null -eq $InputObject) { return $null }

    # Strings are IEnumerable (of chars), so they have to be settled before the collection branch.
    if ($InputObject -is [string] -or $InputObject -is [ValueType]) {
        return $InputObject
    }

    # Dictionaries are also IEnumerable, so they come before it too.
    if ($InputObject -is [System.Collections.IDictionary]) {
        $clean = [ordered]@{}
        foreach ($key in $InputObject.Keys) {
            if ($null -eq $InputObject[$key]) { continue }
            $clean[$key] = Remove-NullProperty $InputObject[$key]
        }
        return [PSCustomObject]$clean
    }

    if ($InputObject -is [System.Collections.IEnumerable]) {
        $items = @(foreach ($item in $InputObject) { Remove-NullProperty $item })
        # Unary comma: without it a one-element array is unrolled to its element by the caller.
        return , $items
    }

    if ($InputObject -is [PSCustomObject]) {
        $clean = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties) {
            if ($null -eq $property.Value) { continue }
            $clean[$property.Name] = Remove-NullProperty $property.Value
        }
        return [PSCustomObject]$clean
    }

    return $InputObject
}

function Get-JsonPath {
    <#
        Walks a chain of property names, returning $null the moment one is absent.

        Set-StrictMode turns reading a missing property into a terminating error, and the CLI's JSON
        shape varies by resource state - a Consumption plan omits numberOfSites, an account with no
        management policy omits the policy object entirely. Probing beats guessing.
    #>
    param(
        $InputObject,
        [Parameter(Mandatory = $true)] [string[]]$Path
    )

    $current = $InputObject
    foreach ($segment in $Path) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }

    return $current
}

function Connect-AzureCliSession {
    <#
        Ensures the CLI is installed, signed in with the management scope, and pointed at the right
        subscription. Signs in interactively when it needs to rather than telling the caller to.
        Returns the selected account object.
    #>
    [CmdletBinding()]
    param(
        [string]$Subscription
    )

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "The Azure CLI is not installed. See https://learn.microsoft.com/cli/azure/install-azure-cli"
    }

    # 'az account show' reads the local token cache and answers even when that token is long dead,
    # so it proves nothing. One cheap call that actually reaches Azure Resource Manager is the only
    # honest test.
    & az account get-access-token --scope "https://management.core.windows.net//.default" --output none 2>&1 | Out-Null
    $signedIn = ($LASTEXITCODE -eq 0)

    if (-not $signedIn) {
        Write-Host "Not signed in to Azure (or the token has expired)." -ForegroundColor Yellow
        Write-Host "Opening a browser to sign you in..." -ForegroundColor Yellow
        Write-Host "  az $($script:ArmLoginArgs -join ' ')"
        Write-Host ""

        # Deliberately NOT wrapped in Invoke-Az: az login prints the browser/device-code prompt and
        # waits for the user, so its output must reach the console rather than a variable.
        & az @script:ArmLoginArgs --output none

        if ($LASTEXITCODE -ne 0) {
            throw ("Azure sign-in did not complete. If no browser is available, run:`n`n" +
                "    az login --use-device-code --scope https://management.core.windows.net//.default")
        }

        Write-Host "Signed in." -ForegroundColor Green
    }

    if (-not [string]::IsNullOrWhiteSpace($Subscription)) {
        Invoke-Az @("account", "set", "--subscription", $Subscription) -AllowFailure | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $available = Invoke-Az @("account", "list", "--query", "[].name", "--output", "tsv")
            throw ("Subscription '$Subscription' is not one this sign-in can see. Pick one of:`n`n" +
                (($available | ForEach-Object { "    $_" }) -join "`n") +
                "`n`nthen re-run with -Subscription `"<name>`".")
        }
    }

    $account = Invoke-Az @("account", "show", "--output", "json") | ConvertFrom-Json
    Write-Host "Subscription: $($account.name) ($($account.id))"
    return $account
}
