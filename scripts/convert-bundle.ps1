#Requires -Version 5.1
<#
.SYNOPSIS
    Converts a downloaded plain UnityFS world bundle (.win) into GLB (glTF 2.0)
    model data for three.js / Unity / Blender, for personal use only.

.DESCRIPTION
    Pipeline (single bundle per run, single-world policy):

        .\scripts\convert-bundle.ps1 -Bundle "output\<worldId>\bundles\<name>.win" [-FetchTool] [-All]

    1. Magic check: the first 8 bytes must be "UnityFS\0" (the same check the
       download manifest reports as fileFormat "unityfs-plain"). Anything else
       is reported honestly and skipped - encrypted bundles are NOT converted,
       decrypted, or guessed at. No decryption exists in this project by design.
    2. AssetRipper (open source, GPL-3.0, pinned version via -RipperVersion) is
       fetched AT RUNTIME into .work/third-party/assetripper/ when -FetchTool is
       given (or reused when already present). It is never vendored into the
       repository. Without -FetchTool the script stops and tells you exactly
       what to run.
    3. The AssetRipper GUI.Free binary is driven headlessly over its local
       WebUI API: POST /LoadFile (synchronous) then POST /Export/PrimaryContent
       which emits one GLB per scene hierarchy object (whole-world model with
       node hierarchy, standard materials and embedded textures), per-mesh
       GLBs, PNG textures, audio and JSON asset dumps.
    4. Deliverables are copied into <worldDir>\converted\ :

           converted\scene\<name>.glb    the world scene model (main output)
           converted\README.txt          personal-use + limits notice
           converted\manifest-<stem>.json  source/output hashes, sizes, counts

       -Textures also copies the decoded PNG textures (converted\textures\).
       -All additionally copies the complete raw export (converted\full-export\).

    Exit codes: 0 = converted; 1 = error (see message + AssetRipper log);
    2 = bundle is not a plain UnityFS file (honest skip - nothing decrypted);
    3 = usage error / tool missing (rerun with -FetchTool to fetch AssetRipper).

    THE CONVERTED DATA IS COPYRIGHTED BY ITS ORIGINAL CREATORS.
    Keep it for personal study/viewing. Do not redistribute, publish, or reuse
    it in other projects - it is not covered by this repository's MIT license.
#>
[CmdletBinding()]
param(
    # Path to a single downloaded unityfs-plain bundle (.win file).
    [Parameter(Mandatory = $true)]
    [string]$Bundle,

    # Explicit opt-in: fetch AssetRipper into .work/third-party (never vendored).
    [switch]$FetchTool,

    # Also copy the decoded texture PNGs (converted\textures\).
    [switch]$Textures,

    # Also keep the complete raw AssetRipper export (converted\full-export\).
    [switch]$All,

    # Per-request (load/export) timeout in minutes.
    [ValidateRange(5, 180)]
    [int]$TimeoutMinutes = 30,

    # Pinned AssetRipper release; override deliberately, never implicitly.
    [string]$RipperVersion = '2.0.0',

    # Optional override for the converted-output root directory.
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-Sha256LowerHex {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-UniqueDestination {
    <#Returns a non-existing sibling path with a -2..-999 suffix when needed.#>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    $directory = Split-Path -Parent $Path
    $stem = [IO.Path]::GetFileNameWithoutExtension($Path)
    $extension = [IO.Path]::GetExtension($Path)
    for ($i = 2; $i -lt 1000; $i++) {
        $candidate = Join-Path $directory ("{0}-{1}{2}" -f $stem, $i, $extension)
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Could not find a free file name for: $Path"
}

function Get-UniqueDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    for ($i = 2; $i -lt 1000; $i++) {
        $candidate = ("{0}-{1}" -f $Path.TrimEnd('\'), $i)
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Could not find a free directory name for: $Path"
}

function Write-MagicBanner {
    param([Parameter(Mandatory)][byte[]]$Lead)
    $hex = ($lead | ForEach-Object { $_.ToString('X2') }) -join ' '
    $ascii = [System.Text.Encoding]::ASCII.GetString($lead) -replace '[^\x20-\x7E]', '.'
    Write-Host ("       first 16 bytes: {0}   ""{1}""" -f $hex, $ascii) -ForegroundColor DarkGray
}

function New-WebUiClient {
    <#HttpClient without auto-redirect so 302 command responses stay observable.#>
    param([TimeSpan]$Timeout)
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = $Timeout
    return $client
}

function Invoke-WebUiPost {
    <#POST a form to the AssetRipper WebUI; accepts 200/302 as success.#>
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)][string]$Endpoint,
        [Parameter(Mandatory)][hashtable]$Fields
    )
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($key in $Fields.Keys) {
        $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new([string]$key, [string]$Fields[$key]))
    }
    $content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    $response = $Client.PostAsync($Endpoint, $content).GetAwaiter().GetResult()
    $status = [int]$response.StatusCode
    if ($status -ne 200 -and $status -ne 302) {
        $body = ''
        try { $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch { }
        throw "AssetRipper WebUI call $Endpoint failed: HTTP $status. $body"
    }
    return $status
}

function Test-UnityFsPlain {
    <#True only when the first 8 bytes are exactly "UnityFS\0".#>
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $lead = New-Object byte[] 16
        $read = $stream.Read($lead, 0, $lead.Length)
        if ($read -lt 16) {
            $short = New-Object byte[] $read
            [Array]::Copy($lead, $short, $read)
            $lead = $short
        }
        return @{ Lead = $lead }
    }
    finally { $stream.Dispose() }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
Import-Module (Join-Path $PSScriptRoot 'lib' 'ClusterMapDownloaderSupport.psm1') -Force -DisableNameChecking
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
$paths = Get-RepoPaths -RepoRoot $repoRoot

try {
    Write-Host 'convert-bundle - downloaded bundle -> GLB (glTF 2.0)' -ForegroundColor Cyan
    Write-Host '      output is for PERSONAL USE ONLY; 3D rights stay with the world creators.' -ForegroundColor Yellow
    Write-Host '      encrypted bundles are never decrypted - this script skips them honestly.' -ForegroundColor Yellow

    # --- 1. bundle + magic check ------------------------------------------
    if (-not (Test-Path -LiteralPath $Bundle -PathType Leaf)) {
        Write-StepFail "Bundle file not found: $Bundle"
        exit 3
    }
    $bundleFullPath = (Resolve-Path -LiteralPath $Bundle).ProviderPath
    $leadInfo = Test-UnityFsPlain -Path $bundleFullPath
    $lead = $leadInfo.Lead
    $isPlain = $lead.Length -ge 8 -and
        $lead[0] -eq 0x55 -and $lead[1] -eq 0x6E -and $lead[2] -eq 0x69 -and $lead[3] -eq 0x74 -and
        $lead[4] -eq 0x79 -and $lead[5] -eq 0x46 -and $lead[6] -eq 0x53 -and $lead[7] -eq 0x00
    if (-not $isPlain) {
        Write-StepFail "Bundle is not plain UnityFS - honest skip (no decryption, ever)."
        Write-MagicBanner -Lead $lead
        Write-StepWarn 'Encrypted files stay exactly as downloaded (see manifest fileFormat).' 
        exit 2
    }

    # --- 2. output location ------------------------------------------------
    # <...>\<worldId>\bundles\<file>.win  ->  <...>\<worldId>\converted\
    $_bundleDir = Split-Path -Parent $bundleFullPath
    if ($OutputDir) {
        $convertedRoot = (New-Item -ItemType Directory -Force -Path $OutputDir).FullName
    }
    elseif ((Split-Path -Leaf $_bundleDir) -eq 'bundles') {
        $worldDir = Split-Path -Parent $_bundleDir
        $convertedRoot = Join-Path $worldDir 'converted'
        $null = New-Item -ItemType Directory -Force -Path $convertedRoot
    }
    else {
        $worldDir = $_bundleDir
        $convertedRoot = Join-Path $worldDir 'converted'
        $null = New-Item -ItemType Directory -Force -Path $convertedRoot
    }
    $bundleStem = [IO.Path]::GetFileNameWithoutExtension($bundleFullPath)

    # --- 3. tool ensure (runtime fetch, never vendored) ---------------------
    $toolDir = Join-Path $paths.ThirdPartyRoot 'assetripper'
    $toolExe = Join-Path $toolDir 'tool' 'AssetRipper.GUI.Free.exe'
    if (-not (Test-Path -LiteralPath $toolExe -PathType Leaf)) {
        if (-not $FetchTool) {
            Write-StepFail 'AssetRipper is not present in .work/third-party/assetripper.'
            Write-Host ('      Run this script again WITH -FetchTool to fetch it once (pinned {0}, GPL-3.0 open source),' -f $RipperVersion) -ForegroundColor Yellow
            Write-Host  '      or download/extract it yourself from https://github.com/AssetRipper/AssetRipper/releases .' -ForegroundColor Yellow
            Write-Host  '      The tool stays in .work (git-ignored); it is never committed. Exit 3.' -ForegroundColor Yellow
            exit 3
        }
        Write-Step ('Fetching AssetRipper {0} (opt-in, runtime fetch, never vendored)...' -f $RipperVersion)
        $null = New-Item -ItemType Directory -Force -Path $toolDir
        $zipPath = Join-Path $toolDir ('AssetRipper_win_x64_{0}.zip' -f $RipperVersion)
        $zipUrl = "https://github.com/AssetRipper/AssetRipper/releases/download/$RipperVersion/AssetRipper_win_x64.zip"
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
            Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
        }
        Write-Step ('Extracting -> {0}\tool ...' -f $toolDir)
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $toolDir 'tool')
        Expand-Archive -Path $zipPath -DestinationPath (Join-Path $toolDir 'tool') -Force
        Write-StepOk ('AssetRipper ready: {0}' -f $toolExe)
        Write-SupportInfo '  AssetRipper is GPL-3.0 open source (https://github.com/AssetRipper/AssetRipper);'
        Write-SupportInfo '  fetched at runtime and never vendored. See NOTICE.md. Converted content remains'
        Write-SupportInfo '  subject to the world creators'' rights: personal use only.'
    }
    else {
        Write-StepOk ('AssetRipper already present: {0}' -f $toolExe)
    }

    # --- 4. start headless WebUI -------------------------------------------
    $port = Get-FreeTcpPort
    $logsDir = Join-Path $toolDir 'logs'
    $null = New-Item -ItemType Directory -Force -Path $logsDir
    $logPath = Join-Path $logsDir ('convert-{0}.log' -f ([IO.Path]::GetRandomFileName() -replace '\.', '-'))
    $rawRoot = Join-Path $paths.WorkRoot 'assetripper-export'
    $null = New-Item -ItemType Directory -Force -Path $rawRoot
    $rawDir = Get-UniqueDirectory -Path (Join-Path $rawRoot $bundleStem)
    $null = New-Item -ItemType Directory -Force -Path $rawDir

    $baseUrl = "http://127.0.0.1:$port"
    Write-Step ('Starting AssetRipper headless WebUI on port {0} ...' -f $port)
    $proc = Start-Process -FilePath $toolExe `
        -ArgumentList @('--headless', '--port', "$port", '--log-path', $logPath) `
        -WorkingDirectory (Split-Path -Parent $toolExe) `
        -PassThru -WindowStyle Hidden
    $timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
    $client = New-WebUiClient -Timeout $timeout

    try {
        $ready = $false
        $probe = New-WebUiClient -Timeout ([TimeSpan]::FromSeconds(3))
        try {
            for ($i = 0; $i -lt 240; $i++) {
                if ($proc.HasExited) { break }
                Start-Sleep -Milliseconds 500
                try {
                    $response = $probe.GetAsync("$baseUrl/").GetAwaiter().GetResult()
                    if ($response.IsSuccessStatusCode) { $ready = $true; break }
                } catch { }
            }
        }
        finally { $probe.Dispose() }
        if (-not $ready) {
            $tail = ''
            try { $tail = (Get-Content -LiteralPath $logPath -Tail 10 | Out-String) } catch { }
            throw ("AssetRipper WebUI did not become ready (PID {0}, exited={1}).{2}Log: {3}{4}{2}Log tail:{2}{5}" -f `
                    $proc.Id, $proc.HasExited, [Environment]::NewLine, $logPath, '', $tail)
        }
        Write-StepOk 'WebUI ready.'

        # --- 5. synchronous load (POST returns after processing) ------------
        Write-Step ('Loading bundle (synchronous; a 100 MB bundle takes a few minutes)...')
        Write-Host ('      {0}' -f $bundleFullPath) -ForegroundColor DarkGray
        $loadStatus = Invoke-WebUiPost -Client $client -Endpoint "$baseUrl/LoadFile" -Fields @{ Path = $bundleFullPath }
        Write-StepOk ("Loaded (HTTP {0})." -f $loadStatus)

        # --- 6. primary content export --------------------------------------
        Write-Step ('Exporting primary content (scene GLB + textures + JSON dumps)...')
        $exportStatus = Invoke-WebUiPost -Client $client -Endpoint "$baseUrl/Export/PrimaryContent" -Fields @{ Path = $rawDir }
        Write-StepOk ("Exported -> {0} (HTTP {1})." -f $rawDir, $exportStatus)
    }
    finally {
        $client.Dispose()
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            try { $null = $proc.WaitForExit(5000) } catch { }
        }
    }

    # --- 7. harvest deliverables -------------------------------------------
    $sceneOut = Join-Path $convertedRoot 'scene'
    $null = New-Item -ItemType Directory -Force -Path $sceneOut
    $sceneGlbs = @(Get-ChildItem -LiteralPath (Join-Path $rawDir 'Assets' 'SceneHierarchyObject') -Filter '*.glb' -File -ErrorAction SilentlyContinue)
    if ($sceneGlbs.Count -eq 0) {
        Write-StepWarn 'No scene GLB was produced. Keeping per-mesh GLBs instead (converted\meshes\).'
        $meshOut = Join-Path $convertedRoot 'meshes'
        $null = New-Item -ItemType Directory -Force -Path $meshOut
        $meshGlbs = @(Get-ChildItem -LiteralPath (Join-Path $rawDir 'Assets' 'Mesh') -Filter '*.glb' -File -ErrorAction SilentlyContinue)
        foreach ($meshGlb in $meshGlbs) {
            Copy-Item -LiteralPath $meshGlb.FullName -Destination (Get-UniqueDestination -Path (Join-Path $meshOut $meshGlb.Name))
        }
        throw 'The bundle produced no scene-level GLB; see the AssetRipper log for details.'
    }

    $outputs = @()
    foreach ($sceneGlb in $sceneGlbs) {
        $destination = Get-UniqueDestination -Path (Join-Path $sceneOut $sceneGlb.Name)
        Copy-Item -LiteralPath $sceneGlb.FullName -Destination $destination
        $outputs += [pscustomobject]@{
            role      = 'scene-glb'
            file      = $destination.Substring($convertedRoot.Length + 1)
            bytes     = (Get-Item -LiteralPath $destination).Length
            sha256    = Get-Sha256LowerHex -Path $destination
        }
    }

    if ($Textures) {
        $texOut = Join-Path $convertedRoot 'textures'
        $null = New-Item -ItemType Directory -Force -Path $texOut
        $textureFiles = @(Get-ChildItem -LiteralPath (Join-Path $rawDir 'Assets' 'Texture2D') -Recurse -File -ErrorAction SilentlyContinue)
        foreach ($texture in $textureFiles) {
            $destination = Get-UniqueDestination -Path (Join-Path $texOut $texture.Name)
            Copy-Item -LiteralPath $texture.FullName -Destination $destination
        }
        Write-StepOk ('Textures copied: {0} file(s) -> converted\textures.' -f $textureFiles.Count)
    }

    if ($All) {
        $fullOut = Join-Path $convertedRoot 'full-export'
        if (Test-Path -LiteralPath $fullOut) { Remove-Item -LiteralPath $fullOut -Recurse -Force }
        Copy-Item -LiteralPath $rawDir -Destination $fullOut -Recurse
        Write-StepOk 'Full raw export copied -> converted\full-export\.'
    }

    # --- 8. manifest + README ----------------------------------------------
    $manifest = [pscustomobject]@{
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        disclaimer  = 'Personal use only. 3D data copyright belongs to the world creators; do not redistribute.'
        source      = [pscustomobject]@{
            bundle     = (Split-Path -Leaf $bundleFullPath)
            fileFormat = 'unityfs-plain'
            bytes      = (Get-Item -LiteralPath $bundleFullPath).Length
            sha256     = Get-Sha256LowerHex -Path $bundleFullPath
        }
        tool        = [pscustomobject]@{
            name    = 'AssetRipper'
            version = $RipperVersion
            license = 'GPL-3.0-or-later'
            mode    = 'runtime-fetched (never vendored); headless WebUI: LoadFile + Export/PrimaryContent'
        }
        outputs     = $outputs
        limits      = @(
            'generic model data only: meshes, materials, textures, node transforms',
            'no cluster gimmick/script behavior in converted models by design',
            'shaders export as placeholder standard materials'
        )
        rawExport   = $rawDir
    }
    $manifestPath = Get-UniqueDestination -Path (Join-Path $convertedRoot ('manifest-{0}.json' -f $bundleStem))
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $readmePath = Join-Path $convertedRoot 'README.txt'
    @'
Converted world data - PERSONAL USE ONLY.

  scene\*.glb : the whole world as one glTF 2.0 model (three.js GLTFLoader,
                Unity via glTF importer (glTFast / UnityGLTF), Blender Drag & Drop).
  textures\   : decoded PNG textures (only when -Textures was given).
  full-export\: complete raw AssetRipper output (only when -All was given).

Limits (honest):
  - Generic model data only: meshes, materials, textures, node transforms.
  - cluster-specific components (gimmicks, colliders in cluster's format,
    spawn logic, scripts) are NOT converted - no behavior.
  - Shaders come out as placeholder standard materials.

The 3D data is copyrighted by the world creators. Keep it for personal
study/viewing; do not redistribute, publish, or reuse it. Converted files
are local-only (output\ is git-ignored) and are never committed.
'@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

    # --- 9. summary ----------------------------------------------------------
    $totalBytes = ($outputs | Measure-Object -Property bytes -Sum).Sum
    Write-SupportSuccess ('Converted. {0} GLB file(s), {1:N1} MB total.' -f $outputs.Count, ($totalBytes / 1MB))
    foreach ($output in $outputs) {
        Write-Host ('      [scene] {0}  sha256={1}  {2:N1} MB' -f $output.file, $output.sha256.Substring(0, 12), ($output.bytes / 1MB)) -ForegroundColor Green
    }
    Write-SupportInfo ('Manifest: {0}' -f (Split-Path -Leaf $manifestPath))
    Write-SupportInfo ('Raw export kept at {0} (delete when done).' -f $rawDir)
    Write-StepWarn 'Reminder: personal use only; the model data is not under this repo''s license.'
}
catch {
    Write-StepFail $_.Exception.Message
    exit 1
}
