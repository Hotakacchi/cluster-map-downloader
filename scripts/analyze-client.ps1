#Requires -Version 5.1
<#
.SYNOPSIS
    Local analysis of an extracted cluster Windows client package (Unity,
    IL2CPP): verifies expected files, optionally produces an Il2CppDumper
    dump, and runs bounded-memory pattern scans over the big binaries plus
    line scans over text artifacts, writing a secret-masked Markdown report.

.DESCRIPTION
    Inputs are expected under the extracted package (default
    .work/client-package):

        cluster.exe
        GameAssembly.dll        (~177 MiB; IL2CPP generated code)
        UnityPlayer.dll
        cluster_Data/global-metadata.dat
        cluster_Data/resources.assets
        cluster_Data/StreamingAssets/aa/catalog.json

    Everything this script writes stays in .work/ (git-ignored):
      .work/analysis/client-analysis-report.md
      .work/il2cpp-dump/ (only with -Il2CppDump)

    The report passes through secret-shape masking (AIza / pk_live / AKIA /
    ya29 / long token query values -> "<REDACTED:...-class>" labels): public
    key value classes stay in prose, their literals never do. NOTICE.md and
    SECURITY.md explain why.
#>
[CmdletBinding()]
param(
    [string]$ClientDirectory,
    [string]$OutputDirectory,
    [switch]$Il2CppDump,
    [string]$Il2CppOutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Add-Rule {
    # Adds a horizontal rule/report section marker into a report list.
    param([System.Collections.Generic.List[string]]$Target, [string]$Heading)
    $Target.Add('')
    $Target.Add(('- {0} -' -f $Heading))
    $Target.Add('')
}

try {
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
    Import-Module (Join-Path $repoRoot 'scripts' 'lib' 'ClusterMapDownloaderSupport.psm1') -Force -DisableNameChecking
    Initialize-Console
    $paths = Get-RepoPaths -RepoRoot $repoRoot
    if (-not $ClientDirectory) { $ClientDirectory = $paths.ClientPackageDir }
    if (-not $OutputDirectory) { $OutputDirectory = $paths.AnalysisDir }
    if (-not $Il2CppOutDir)    { $Il2CppOutDir = $paths.Il2CppDumpDir }

    $clientRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ClientDirectory)
    $reportRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null

    Write-SupportInfo ("analyze-client — client package: {0}" -f $clientRoot)
    if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) {
        throw "Client package directory not found: $clientRoot (see scripts/extract-installer.ps1)."
    }

    # ---- 1. expected files ------------------------------------------------
    Write-Step 'Checking expected client package files'
    $expectedRelatives = @(
        'cluster.exe',
        'GameAssembly.dll',
        'UnityPlayer.dll',
        'cluster_Data\global-metadata.dat',
        'cluster_Data\resources.assets',
        'cluster_Data\StreamingAssets\aa\catalog.json',
        'cluster_Data\StreamingAssets\aa\catalog.hash'
    )
    $present = @{}
    foreach ($relative in $expectedRelatives) {
        $full = Join-Path $clientRoot $relative
        $present[$relative] = (Test-Path -LiteralPath $full -PathType Leaf)
        if ($present[$relative]) {
            Write-StepOk ("{0} ({1})" -f $relative, (Format-ByteSize ((Get-Item -LiteralPath $full).Length)))
        }
        else {
            Write-StepWarn "MISSING: $relative"
        }
    }
    if (-not $present['GameAssembly.dll']) {
        throw "GameAssembly.dll not found — is that really an extracted cluster Windows client?"
    }

    $report = [System.Collections.Generic.List[string]]::new()
    $report.Add('# cluster Windows client — local analysis report')
    $report.Add('')
    $report.Add(('Generated: {0}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss "UTC"')))
    $report.Add('')
    $report.Add(('**Client package:** `' + $clientRoot + '`'))
    $report.Add('')
    $report.Add('Scope and boundaries: local-only artifacts (whole project ignores .work/, ' +
                '.temp/, downloads/, output/). Secret-shaped strings get masked to value-class ' +
                'labels in this report; no public-key*** or token literals are emitted. ' +
                'See NOTICE.md and SECURITY.md.')
    $report.Add('')

    # ---- 2. (-Il2CppDump) optional dump ------------------------------------
    $il2CppDumpGenerated = $false
    if ($Il2CppDump) {
        Write-Step 'Il2CppDumper: generating the dump (output stays in .work/il2cpp-dump)'
        $dumperDir = Confirm-Il2CppDumperTool
        $gameAssembly = Join-Path $clientRoot 'GameAssembly.dll'
        $metadataFile = Join-Path $clientRoot 'cluster_Data\global-metadata.dat'
        if (-not (Test-Path -LiteralPath $metadataFile -PathType Leaf)) {
            throw "global-metadata.dat missing at $metadataFile — cannot run Il2CppDumper."
        }
        New-Item -ItemType Directory -Force -Path $Il2CppOutDir | Out-Null
        & dotnet (Join-Path $dumperDir 'Il2CppDumper.dll') $gameAssembly $metadataFile $Il2CppOutDir
        if ($LASTEXITCODE -ne 0) { throw "Il2CppDumper failed with exit code $LASTEXITCODE" }
        Write-StepOk ("IL2CPP dump written under {0}" -f $Il2CppOutDir)
        $il2CppDumpGenerated = $true
    }

    # ---- 3. binary scanning (chunked, memory bounded) ----------------------
    Write-Step 'Pattern scanning binaries (4 MiB windows, per-pattern caps, masked contexts)'
    $binaryPatterns = [ordered]@{
        'api.cluster.mu host'      = [regex]::Escape('api.cluster.mu')
        'update.cluster.mu host'   = [regex]::Escape('update.cluster.mu')
        'file-storage/imgix cdn'   = 'cluster-file-storage|imgix\.net'
        'telemetry cl4r.net'       = 'cl4r\.net'
        'firebase/google hosts'    = 'firebaseapp\.com|cloudfunctions\.net|firebaseio\.com|googleapis\.com'
        'rooms route'              = '/v1/rooms'
        'enter_v2 payload route'   = 'enter_v2'
        'enter_info payload route' = 'enter_info'
        'worlds route'             = '/v1/worlds'
        'venues route'             = '/v1/venues'
        'platformResources field'  = 'platformResources'
        'assetBundleUrl field'     = 'assetBundleUrl'
        'assetIdsDependsOn field'  = 'assetIdsDependsOn'
        'unitySceneName field'     = 'unitySceneName'
        'RoomVenue type'           = 'RoomVenue'
        'RoomEnterResponse type'   = 'RoomEnterResponse'
        'UnityResource type'       = 'UnityResource'
        'UnityAsset type'          = 'UnityAsset'
        'VenueAssetBundleLoaderV2' = 'VenueAssetBundleLoaderV2'
        'AesAssetDecryptor'        = 'AesAssetDecryptor'
        'IAssetDecryptor'          = 'IAssetDecryptor'
        'client api classes'       = 'RimuldarApiClient|ClusterVR\.Infrastructure\.Api\.Client'
        'x-cluster headers'        = 'X-Cluster-[A-Za-z-]{3,}'
        'Authorization header'     = 'Authorization'
        'BAD_TOKEN_EXIST code'     = 'BAD_TOKEN_EXIST'
        'app version tokens'       = '3\.10[0-9]\.[0-9]{6,}'
        'rc/client version tag'    = 'rc/client/v[0-9][0-9.]*'
        'roomasset bundles'        = 'roomasset(builtin|urp)'
        'addressables catalog'     = 'catalog\.(json|hash)|Addressables'
        'http(s) urls'             = 'https?://[0-9A-Za-z._~:/?#\[\]@!$&*+,;=%-]{6,}'
    }
    $binaryFiles = @()
    foreach ($relative in @('GameAssembly.dll', 'cluster_Data\global-metadata.dat', 'cluster_Data\resources.assets')) {
        if ($present[$relative]) { $binaryFiles += (Join-Path $clientRoot $relative) }
    }

    foreach ($binaryFile in $binaryFiles) {
        Write-SupportInfo ("scanning {0} …" -f (Split-Path -Leaf $binaryFile))
        $hits = Invoke-FileScan -Path $binaryFile -Patterns $binaryPatterns -ContextBytes 96 -MaxMatchesPerPattern 12
        $perPattern = $hits | Group-Object -Property 'Pattern' | Sort-Object Name
        Add-Rule $report ("binary scan: " + (Split-Path -Leaf $binaryFile))
        $report.Add('| Pattern | Hits (capped) | Sample offsets |')
        $report.Add('|---|---|---|')
        foreach ($group in $perPattern) {
            $samples = ($group.Group | Select-Object -First 5 | ForEach-Object { $_.Offset }) -join ', '
            $report.Add(('| {0} | {1} | {2} |' -f $group.Name, $group.Count, $samples))
        }
        $report.Add('')
        $report.Add('First context excerpts (masked, non-ASCII rendered as "."):')
        $report.Add('')
        foreach ($hit in ($hits | Select-Object -First 40)) {
            $context = $hit.Context -replace '[\|`]', ' '
            $report.Add('- **{0}** @ {1}: `{2}`' -f $hit.Pattern, $hit.Offset, $context)
        }
        Write-StepOk ("{0}: {1} pattern hit groups" -f (Split-Path -Leaf $binaryFile), ($perPattern | Measure-Object).Count)
    }
    if ($binaryFiles.Count -eq 0) {
        Add-Rule $report 'binary scan'
        $report.Add('_No binary files were found to scan._')
    }

    # ---- 4. text artifacts: catalog + (optional) IL2CPP dump --------------
    $catalogJson = Join-Path $clientRoot 'cluster_Data\StreamingAssets\aa\catalog.json'
    if ($present['cluster_Data\StreamingAssets\aa\catalog.json']) {
        Write-Step ('scanning addressables catalog (text): {0}' -f (Split-Path -Leaf $catalogJson))
        $catalogPatterns = [ordered]@{
            'http url'   = 'https?://[^"\s\\]+'
            'bundle key' = 'roomasset(builtin|urp)[^"\s]*'
            'builtin key' = '(globalshader|monoscripts|unitybuiltin|addressables?)'
        }
        $catalogHits = Find-TextLineMatches -Path $catalogJson -Patterns $catalogPatterns -MaxPerPattern 30
        Add-Rule $report ('text scan: ' + (Split-Path -Leaf $catalogJson))
        $currentPattern = $null
        foreach ($hit in $catalogHits) {
            if ($currentPattern -ne $hit.Pattern) {
                $currentPattern = $hit.Pattern
                Add-Rule $report ($currentPattern)
            }
            $excerpt = $hit.Excerpt -replace '[\|`]', ' '
            $report.Add(('- {0}: `{1}`' -f $hit.Line, $excerpt))
        }
        Write-StepOk ('{0}: {1} hits' -f (Split-Path -Leaf $catalogJson), ($catalogHits | Measure-Object).Count)
    }

    $il2CppDumpCs = Join-Path $Il2CppOutDir 'dump.cs'
    $il2CppLiteralJson = Join-Path $Il2CppOutDir 'stringliteral.json'
    if ((Test-Path -LiteralPath $il2CppDumpCs -PathType Leaf) -or $il2CppDumpGenerated) {
        if (Test-Path -LiteralPath $il2CppDumpCs -PathType Leaf) {
            Write-Step ('scanning IL2CPP dump.cs (text, streamed): {0}' -f $il2CppDumpCs)
            $dumpPatterns = [ordered]@{
                'RoomVenue'              = 'RoomVenue'
                'RoomEnterResponse'      = 'RoomEnterResponse'
                'platformResources'      = 'platformResources'
                'assetBundleUrl'         = 'assetBundleUrl'
                'assetBundleName'        = 'assetBundleName'
                'assetIdsDependsOn'      = 'assetIdsDependsOn'
                'unitySceneName'         = 'unitySceneName'
                'enter_v2'               = 'enter_v2'
                'enter_info'             = 'enter_info'
                'EnterToRoom method'     = 'EnterToRoom'
                'GetEnterInfo method'    = 'GetEnterInfo'
                'VenueAssetBundleLoaderV2' = 'VenueAssetBundleLoaderV2'
                'BundleLoader'           = 'BundleLoader'
                'LoadAsync method'       = 'LoadAsync\('
                'AesAssetDecryptor'      = 'AesAssetDecryptor'
                'IAssetDecryptor'        = 'IAssetDecryptor'
                'RimuldarApiClient'      = 'RimuldarApiClient'
                'ClusterVR api ns'       = 'ClusterVR\.Infrastructure\.Api\.Client'
                'v1 routes'              = '/v1/[A-Za-z0-9_{}]+'
                'x-cluster headers'      = 'X-Cluster-[A-Za-z-]{3,}'
                'rc/client version tag'  = 'rc/client/v[0-9][0-9.]*'
            }
            $dumpHits = Find-TextLineMatches -Path $il2CppDumpCs -Patterns $dumpPatterns -MaxPerPattern 25
            Add-Rule $report ('text scan: IL2CPP dump.cs')
            $currentPattern = $null
            foreach ($hit in $dumpHits) {
                if ($currentPattern -ne $hit.Pattern) {
                    $currentPattern = $hit.Pattern
                    Add-Rule $report ($currentPattern)
                }
                $excerpt = $hit.Excerpt -replace '[\|`]', ' '
                $report.Add(('- {0}: `{1}`' -f $hit.Line, $excerpt))
            }
            Write-StepOk ('dump.cs: {0} hits' -f ($dumpHits | Measure-Object).Count)
        }
        else {
            Write-StepWarn ('dump.cs is missing although the dump dir exists — run analyze-client again with -Il2CppDump.')
        }
    }
    else {
        Write-StepWarn ('No IL2CPP dump yet: re-run with -Il2CppDump to also scan dump.cs (class/method chains).')
    }

    if (Test-Path -LiteralPath $il2CppLiteralJson -PathType Leaf) {
        Write-Step ('scanning stringliteral.json (text, streamed): {0}' -f (Split-Path -Leaf $il2CppLiteralJson))
        $literalPatterns = [ordered]@{
            'v1 routes'    = '/v1/[A-Za-z0-9_{}]+'
            'enter routes' = 'enter_(v2|info)'
            'headers'      = 'X-Cluster-[A-Za-z-]{3,}'
        }
        $literalHits = Find-TextLineMatches -Path $il2CppLiteralJson -Patterns $literalPatterns -MaxPerPattern 40
        Add-Rule $report ('text scan: IL2CPP stringliteral.json')
        $currentPattern = $null
        foreach ($hit in $literalHits) {
            if ($currentPattern -ne $hit.Pattern) {
                $currentPattern = $hit.Pattern
                Add-Rule $report ($currentPattern)
            }
            $excerpt = $hit.Excerpt -replace '[\|`]', ' '
            $report.Add(('- {0}: `{1}`' -f $hit.Line, $excerpt))
        }
        Write-StepOk ('stringliteral.json: {0} hits' -f ($literalHits | Measure-Object).Count)
    }

    # ---- 5. save the report -------------------------------------------------
    $reportPath = Join-Path $reportRoot 'client-analysis-report.md'
    Set-Content -LiteralPath $reportPath -Value ($report -join [Environment]::NewLine) -Encoding UTF8
    Write-StepOk ("analysis report written: {0}" -f $reportPath)
    Write-SupportInfo  ('Reminder: reports and dumps stay in git-ignored directories; ' +
                        'client/world content remains the creators'' property — keep it local.')
    Write-SupportSuccess 'analyze-client completed.'
    exit 0
}
catch {
    Write-StepFail ("analyze-client failed: {0}" -f $_.Exception.Message)
    exit 1
}
