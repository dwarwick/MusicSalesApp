<#
.SYNOPSIS
    Creates and configures the audio-processing Function App in Azure for one environment.

.DESCRIPTION
    Creates the Function App (Windows Consumption, Functions v4, .NET 10 isolated), wires up
    Application Insights, creates the two queues, and merges in the staging lifecycle rule.

    App settings are pushed from appsettings.{Environment}.json only on the run that CREATES the
    app, because a brand-new Function App has no connection strings and its triggers cannot bind
    without them. Afterwards the Azure portal is authoritative and this script only *reports* drift
    between the two - keeping nine values in sync across two places is how a portal edit ends up
    silently reverted. -ApplySettings overrides that and pushes the file up.

    Idempotent throughout - safe to re-run against an environment that already exists. That matters
    because Test and Production share one storage account, so provisioning the second environment
    must not disturb the first. In particular the lifecycle rule is *merged* into any existing
    management policy rather than replacing it, since a policy is account-wide and holds the rules
    for both environments.

    The queues are created here even though the application code would create them on first use
    (MediaProcessingQueueClient calls CreateIfNotExistsAsync before every send). Leaving it to the
    code means a freshly provisioned environment shows no queues in the portal until the web app
    happens to enqueue something, which looks broken and depends on deploy order.

    The transcode POISON queue is created here for a sharper reason: HandleTranscodePoison triggers
    on it, and a queue trigger does NOT create a queue that is absent - verified on Test, where the
    listener started cleanly but the queue only appeared once created by hand. Left alone it would
    spring into existence at the worst possible moment, the first time a real upload poisoned. The
    remaining poison queues and the staging container are still created on demand, by the Functions
    runtime and by SongUploadJobService respectively.

    Requires the Azure CLI. Signs you in interactively if you are not already.

.EXAMPLE
    .\Provision-FunctionApp.ps1 -Environment Test -WhatIf
    Shows what would be created without touching anything.

.EXAMPLE
    .\Provision-FunctionApp.ps1 -Environment Test
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

    [switch]$SkipAppInsights,

    # Settings are pushed automatically only when this run creates the app. Once an environment
    # exists the portal is authoritative; this forces the appsettings file back over it.
    [switch]$ApplySettings
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "AzureCli.ps1")
. (Join-Path $PSScriptRoot "Get-MediaProcessingSettings.ps1")

# Signs in interactively if needed, and selects the subscription explicitly rather than inheriting
# whatever `az account set` was last run for some other project.
$account = Connect-AzureCliSession -Subscription $Subscription

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
$mediaAccountName = Get-AccountNameFromConnectionString $values["MediaStorageConnectionString"]

Write-Host "Function app  : $FunctionAppName"
Write-Host "Queues/staging: $stagingAccountName (must be Standard general-purpose v2)"
Write-Host "Media         : $mediaAccountName (premium; read-only from the Function)"

# ---------------------------------------------------------------------------
# Locate the staging account, and confirm it can actually host queues.
# ---------------------------------------------------------------------------
function Get-StorageAccountOrExplain {
    param([string]$Name, [string]$Role)

    $result = Invoke-Az @("storage", "account", "show", "--name", $Name, "--output", "json") -AllowFailure
    if ($LASTEXITCODE -eq 0) {
        return $result | ConvertFrom-Json
    }

    # Almost always the wrong subscription rather than a genuinely missing account, and the raw CLI
    # error does not suggest that.
    throw ("Storage account '$Name' ($Role) was not found in subscription " +
        "'$($account.name)'.`n`nIf StreamTunes' storage lives elsewhere, re-run with " +
        "-Subscription `"<name>`". Available:`n`n" +
        ((Invoke-Az @("account", "list", "--query", "[].name", "--output", "tsv") | ForEach-Object { "    $_" }) -join "`n"))
}

$stagingAccount = Get-StorageAccountOrExplain -Name $stagingAccountName -Role "queues and upload staging"

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) { $ResourceGroup = $stagingAccount.resourceGroup }
if ([string]::IsNullOrWhiteSpace($Location)) { $Location = $stagingAccount.location }

Write-Host "Resource group: $ResourceGroup"
Write-Host "Location      : $Location"

# This is the constraint that forced two storage accounts in the first place. Fail here with a
# legible message rather than at runtime with an obscure binding error.
if ($stagingAccount.sku.tier -ne "Standard" -or $stagingAccount.kind -ne "StorageV2") {
    throw ("'$stagingAccountName' is $($stagingAccount.kind)/$($stagingAccount.sku.tier). " +
        "The queues need a Standard general-purpose v2 account - no premium account type offers " +
        "the Queue service.")
}

$mediaAccount = Get-StorageAccountOrExplain -Name $mediaAccountName -Role "song media"

if ($mediaAccount.location -ne $Location) {
    # Not fatal, but the probe path downloads every playback blob during an audit, and the assembly
    # step copies across accounts. Both are free in-region and billed cross-region.
    Write-Warning ("The media account is in '$($mediaAccount.location)' but the Function App will be " +
        "in '$Location'. Cross-region egress will be billed on every audit and every upload.")
}

# ---------------------------------------------------------------------------
# Resource providers
#
# A subscription that has only ever hosted storage accounts will not have these registered, and the
# failure is badly mislabelled: creating Application Insights reports a "Conflict" naming an inner
# MissingProviderRegistrationException. Registering is idempotent and free.
#
# microsoft.operationalinsights is the non-obvious one - Application Insights is workspace-based
# now, so it needs Log Analytics even though nothing here mentions Log Analytics.
# ---------------------------------------------------------------------------
$requiredProviders = @("Microsoft.Web", "microsoft.insights")
if (-not $SkipAppInsights) { $requiredProviders += "microsoft.operationalinsights" }

foreach ($namespace in $requiredProviders) {
    $state = (Invoke-Az @("provider", "show", "--namespace", $namespace, "--query", "registrationState", "--output", "tsv") -AllowFailure | Out-String).Trim()

    if ($state -eq "Registered") {
        continue
    }

    if ($PSCmdlet.ShouldProcess($namespace, "Register resource provider")) {
        Write-Host "Registering resource provider '$namespace' (one-off, can take a minute)..."
        Invoke-Az @("provider", "register", "--namespace", $namespace, "--wait") | Out-Null
    }
}

# ---------------------------------------------------------------------------
# Application Insights
# ---------------------------------------------------------------------------
$appInsightsKey = $null

# Declared up front because under -WhatIf the create block below never runs, and Set-StrictMode
# treats reading a never-assigned variable as an error.
$appInsights = $null

if (-not $SkipAppInsights) {
    if ([string]::IsNullOrWhiteSpace($AppInsightsName)) { $AppInsightsName = $FunctionAppName }

    $existing = Invoke-Az @(
        "monitor", "app-insights", "component", "show",
        "--app", $AppInsightsName,
        "--resource-group", $ResourceGroup,
        "--output", "json") -AllowFailure

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Application Insights '$AppInsightsName' already exists."
        $appInsights = $existing | ConvertFrom-Json
    }
    elseif ($PSCmdlet.ShouldProcess($AppInsightsName, "Create Application Insights")) {
        Write-Host "Creating Application Insights '$AppInsightsName'..."
        $appInsights = Invoke-Az @(
            "monitor", "app-insights", "component", "create",
            "--app", $AppInsightsName,
            "--location", $Location,
            "--resource-group", $ResourceGroup,
            "--application-type", "web",
            "--output", "json") | ConvertFrom-Json
    }

    if ($null -ne $appInsights) { $appInsightsKey = $appInsights.connectionString }
}

# ---------------------------------------------------------------------------
# The Function App itself
# ---------------------------------------------------------------------------
$existingApp = Invoke-Az @(
    "functionapp", "show",
    "--name", $FunctionAppName,
    "--resource-group", $ResourceGroup,
    "--output", "json") -AllowFailure

$appAlreadyExisted = ($LASTEXITCODE -eq 0)

if ($appAlreadyExisted) {
    Write-Host "Function App '$FunctionAppName' already exists; updating its configuration."
}
elseif ($PSCmdlet.ShouldProcess($FunctionAppName, "Create Windows Consumption Function App")) {
    Write-Host "Creating Function App '$FunctionAppName'..."

    # Windows on purpose: ffmpeg.exe is a Windows binary and ships inside the deployment package.
    # Linux Consumption does not support .NET 10 at all and is being retired.
    $createArgs = @(
        "functionapp", "create",
        "--name", $FunctionAppName,
        "--resource-group", $ResourceGroup,
        "--consumption-plan-location", $Location,
        "--os-type", "Windows",
        "--runtime", "dotnet-isolated",
        "--functions-version", "4",
        "--storage-account", $stagingAccountName,
        "--output", "json")

    if (-not [string]::IsNullOrWhiteSpace($appInsightsKey)) {
        $createArgs += @("--app-insights", $AppInsightsName)
    }
    else {
        $createArgs += "--disable-app-insights"
    }

    Invoke-Az $createArgs | Out-Null
}

# Pinned separately rather than via --runtime-version, which has not accepted "10" on every CLI
# version. This is the setting the Windows host actually reads.
if ($PSCmdlet.ShouldProcess($FunctionAppName, "Pin the worker to .NET 10")) {
    Invoke-Az @(
        "functionapp", "config", "set",
        "--name", $FunctionAppName,
        "--resource-group", $ResourceGroup,
        "--net-framework-version", "v10.0",
        "--output", "none") | Out-Null
}

# ---------------------------------------------------------------------------
# App settings
#
# Applied only when this run CREATED the app - a brand-new Function App has no connection strings
# and no queue names, so its triggers cannot even bind until these exist.
#
# For an app that already exists, the Azure portal is authoritative and this script does not touch
# settings. That is a deliberate choice: maintaining the same nine values in two places invites
# exactly the drift where an edit made in one is silently reverted by the other. Pass
# -ApplySettings to push the appsettings file up anyway.
# ---------------------------------------------------------------------------
$shouldApplySettings = $ApplySettings -or (-not $appAlreadyExisted)

if ($shouldApplySettings -and $PSCmdlet.ShouldProcess($FunctionAppName, "Apply application settings")) {
    Write-Host "Applying application settings..."

    $settingPairs = $values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }

    # Read-only package mount. This is why the Function points FFMpegCore's temp folder at %TEMP%
    # rather than writing beside the binary.
    $settingPairs += "WEBSITE_RUN_FROM_PACKAGE=1"

    $applyArgs = @(
        "functionapp", "config", "appsettings", "set",
        "--name", $FunctionAppName,
        "--resource-group", $ResourceGroup,
        "--output", "none",
        "--settings") + $settingPairs

    Invoke-Az $applyArgs | Out-Null
}
elseif ($appAlreadyExisted) {
    Write-Host "Leaving application settings alone; the portal is authoritative."

    # Read-only drift check. The web app and the Function have to agree on the queue names, the
    # staging container and the API key - if they diverge, messages land in a queue nobody polls or
    # every callback 401s, and neither failure announces itself. Reported, never corrected.
    $liveJson = Invoke-Az @(
        "functionapp", "config", "appsettings", "list",
        "--name", $FunctionAppName,
        "--resource-group", $ResourceGroup,
        "--output", "json") -AllowFailure

    if ($LASTEXITCODE -eq 0) {
        $live = @{}
        foreach ($setting in (($liveJson | Out-String) | ConvertFrom-Json)) {
            $live[$setting.name] = $setting.value
        }

        $drift = @()
        foreach ($entry in $values.GetEnumerator()) {
            if (-not $live.ContainsKey($entry.Key)) {
                $drift += "$($entry.Key) is missing in Azure"
            }
            elseif ($live[$entry.Key] -ne $entry.Value) {
                # Values are secrets; report the key only.
                $drift += "$($entry.Key) differs from appsettings.$Environment.json"
            }
        }

        if ($drift.Count -gt 0) {
            Write-Warning ("Azure and appsettings.$Environment.json disagree on $($drift.Count) setting(s):`n" +
                (($drift | ForEach-Object { "    $_" }) -join "`n") +
                "`n`nNothing was changed. The web app reads the file, the Function reads Azure - they must " +
                "agree on the queue names, the staging container and the API key. Re-run with " +
                "-ApplySettings to make Azure match the file.")
        }
        else {
            Write-Host "Settings in Azure match appsettings.$Environment.json."
        }
    }
}

# ---------------------------------------------------------------------------
# Queues
#
# The application code creates these on first use, but that leaves a window where the deployed
# system looks broken: the portal shows no queues, and there is nothing for the scale controller to
# watch until the web app happens to enqueue something. Creating them here makes the provisioned
# state self-evident and independent of deploy order. Both calls are idempotent.
# ---------------------------------------------------------------------------
$queuesToCreate = @(
    $values["MediaProcessing:TranscodeQueueName"],
    $values["MediaProcessing:ProbeQueueName"],
    $values["MediaProcessing:MatchQueueName"],

    # HandleTranscodePoison triggers on this one, and a queue trigger will not create it. Its name
    # is the transcode queue's plus "-poison" - the same expression the trigger binds to - so the
    # two cannot drift.
    "$($values["MediaProcessing:TranscodeQueueName"])-poison"
)

foreach ($queueName in $queuesToCreate) {
    if ($PSCmdlet.ShouldProcess("$stagingAccountName/$queueName", "Create queue")) {
        Invoke-Az @(
            "storage", "queue", "create",
            "--name", $queueName,
            "--account-name", $stagingAccountName,
            "--connection-string", $values["StagingStorageConnectionString"],
            "--only-show-errors",
            "--output", "none") | Out-Null
        Write-Host "Queue '$queueName' is present."
    }
}

# ---------------------------------------------------------------------------
# Staging container
#
# Created here as well as by the app. SongUploadJobService and CoverArtMatchService both call
# CreateIfNotExistsAsync before writing, which is enough while the web server does the writing - but
# a browser holding a write SAS has no such opportunity, and a missing container surfaces to it as an
# opaque 404 with nothing server-side to log. Same argument as the queues above.
# ---------------------------------------------------------------------------
$stagingContainer = $values["MediaProcessing:StagingContainerName"]

if ($PSCmdlet.ShouldProcess("$stagingAccountName/$stagingContainer", "Create container")) {
    Invoke-Az @(
        "storage", "container", "create",
        "--name", $stagingContainer,
        "--connection-string", $values["StagingStorageConnectionString"],
        "--public-access", "off",
        "--only-show-errors",
        "--output", "none") | Out-Null
    Write-Host "Container '$stagingContainer' is present (public access off)."
}

# ---------------------------------------------------------------------------
# Blob CORS, so the browser can PUT directly to staging
#
# Without this a browser upload fails at the preflight with nothing in Serilog, Application Insights
# or Azure - the request never reaches anything that logs.
#
# TWO THINGS TO UNDERSTAND BEFORE CHANGING THIS.
#
# It is account-and-service scoped, not container scoped. Test and Production share this storage
# account, so enabling CORS for one enables it on the other's account in the same call. That is
# acceptable because CORS is not authentication - every blob still requires a SAS and no container
# has public access - but it does mean this step cannot be confined to one environment. The app-level
# feature flag is what makes the rollouts independent.
#
# And `az storage cors add` APPENDS. Re-running without the check below duplicates the rule every
# time. Deliberately no `cors clear`: that would wipe the other environment's rule, exactly the
# hazard the lifecycle-rule merge below exists to avoid.
# ---------------------------------------------------------------------------
$callbackBase = ($values["CallbackBaseUrl"]).TrimEnd('/')
$callbackUri = [Uri]$callbackBase

# Not $host - that is an automatic variable holding the PowerShell host object.
$originHost = $callbackUri.Host
$corsOrigins = @("$($callbackUri.Scheme)://$originHost")
if (-not $originHost.StartsWith("www.")) {
    $corsOrigins += "$($callbackUri.Scheme)://www.$originHost"
}

# Local dev talks to the Test environment's account. Production gets no localhost origin: it would
# buy nothing (a SAS is still required) and widens who can spend a leaked one.
if ($Environment -ne "Production") {
    $corsOrigins += @("https://localhost:7217", "http://localhost:5000", "https://localhost:5001")
}

$existingCorsJson = Invoke-Az @(
    "storage", "cors", "list",
    "--services", "b",
    "--connection-string", $values["StagingStorageConnectionString"],
    "--output", "json") -AllowFailure

$existingOrigins = @()
if ($LASTEXITCODE -eq 0) {
    $parsedCors = ($existingCorsJson | Out-String) | ConvertFrom-Json
    # Projected explicitly rather than via member enumeration, for the same Set-StrictMode reason the
    # lifecycle block documents below - an account with no CORS rules yet returns an empty array.
    foreach ($rule in @($parsedCors)) {
        $ruleOrigins = Get-JsonPath $rule @("AllowedOrigins")
        if ($null -eq $ruleOrigins) { continue }

        # `az storage cors list` returns AllowedOrigins as a comma-delimited STRING, not the array
        # the rest of its JSON would lead you to expect. Treating it as an array silently matches
        # nothing, so every run looks like a fresh account and appends the rule again - and `cors
        # add` appends, so the duplicates accumulate. Both shapes handled in case the CLI changes.
        if ($ruleOrigins -is [string]) {
            $existingOrigins += @($ruleOrigins -split ',' | ForEach-Object { $_.Trim() })
        }
        else {
            $existingOrigins += @($ruleOrigins | ForEach-Object { "$_".Trim() })
        }
    }
}

$missingOrigins = @($corsOrigins | Where-Object { $existingOrigins -notcontains $_ })

if ($missingOrigins.Count -eq 0) {
    Write-Host "Blob CORS already allows $($corsOrigins -join ', ')."
}
elseif ($PSCmdlet.ShouldProcess($stagingAccountName, "Allow blob CORS from $($missingOrigins -join ', ')")) {
    # Allowed and exposed headers are '*' deliberately, while origins never are. The Azure storage
    # JS SDK's header set shifts between versions, and a missing allowed-header surfaces as the same
    # opaque preflight failure as no CORS at all. Origins stay explicit because they are the part
    # that actually decides who may spend a leaked SAS.
    # Built as one array first: `Invoke-Az @(...) + $more` would call Invoke-Az with the first array
    # and then try to add to its result.
    $corsArgs = @(
        "storage", "cors", "add",
        "--services", "b",
        "--methods", "PUT", "OPTIONS",
        "--origins") + $missingOrigins + @(
        "--allowed-headers", "*",
        "--exposed-headers", "*",
        "--max-age", "3600",
        "--connection-string", $values["StagingStorageConnectionString"],
        "--output", "none")

    Invoke-Az $corsArgs | Out-Null

    Write-Host "Blob CORS now allows $($missingOrigins -join ', ')."
}

# ---------------------------------------------------------------------------
# Staging lifecycle rule
#
# A management policy is account-wide and holds ALL rules for the account, so this has to merge
# rather than overwrite - Test and Production share one storage account, which means provisioning
# the second environment must not wipe the first one's rule.
# ---------------------------------------------------------------------------
$ruleName = "delete-abandoned-$stagingContainer"

$existingPolicyJson = Invoke-Az @(
    "storage", "account", "management-policy", "show",
    "--account-name", $stagingAccountName,
    "--resource-group", $ResourceGroup,
    "--output", "json") -AllowFailure

$existingRules = @()
if ($LASTEXITCODE -eq 0) {
    $parsed = ($existingPolicyJson | Out-String) | ConvertFrom-Json
    $rules = Get-JsonPath $parsed @("policy", "rules")
    if ($null -ne $rules) { $existingRules = @($rules) }
}

# Projected explicitly rather than via $existingRules.Name: under Set-StrictMode, member enumeration
# over an empty array throws "The property 'Name' cannot be found on this object" - which is exactly
# the state a freshly torn-down account is in.
$existingRuleNames = @($existingRules | ForEach-Object { $_.name })

if ($existingRuleNames -contains $ruleName) {
    Write-Host "Lifecycle rule '$ruleName' is already present."
}
elseif ($PSCmdlet.ShouldProcess($stagingAccountName, "Add a 7-day delete rule for $stagingContainer")) {
    $newRule = [ordered]@{
        enabled    = $true
        name       = $ruleName
        type       = "Lifecycle"
        definition = [ordered]@{
            actions = @{ baseBlob = @{ delete = @{ daysAfterModificationGreaterThan = 7 } } }
            filters = [ordered]@{
                blobTypes   = @("blockBlob")
                prefixMatch = @("$stagingContainer/")
            }
        }
    }

    # Round-tripping keeps any rule this script did not write. Nulls are stripped because the CLI
    # returns every optional field it knows about as an explicit null, and feeding those back can be
    # rejected as an invalid policy.
    $merged = @{ rules = @(@($existingRules | ForEach-Object { Remove-NullProperty $_ }) + $newRule) }

    $policyPath = Join-Path ([System.IO.Path]::GetTempPath()) "staging-lifecycle-$([Guid]::NewGuid().ToString('N')).json"
    try {
        Set-Content -LiteralPath $policyPath -Value ($merged | ConvertTo-Json -Depth 12) -Encoding UTF8
        Invoke-Az @(
            "storage", "account", "management-policy", "create",
            "--account-name", $stagingAccountName,
            "--resource-group", $ResourceGroup,
            "--policy", "@$policyPath",
            "--output", "none") | Out-Null

        if ($existingRules.Count -gt 0) {
            Write-Host "Merged a 7-day delete rule for '$stagingContainer' alongside $($existingRules.Count) existing rule(s)."
        }
        else {
            Write-Host "Added a 7-day delete rule for abandoned '$stagingContainer' blobs."
        }
    }
    finally {
        Remove-Item -LiteralPath $policyPath -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Done. Next:" -ForegroundColor Cyan
Write-Host "  1. Deploy the web app first (.vscode publish task), so it understands every step the"
Write-Host "     Function can report before the Function starts reporting them."
Write-Host "  2. Deploy the code:  pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName $FunctionAppName"
Write-Host "  3. Upload a song and watch the progress bar."
