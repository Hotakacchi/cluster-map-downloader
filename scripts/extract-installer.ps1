#Requires -Version 5.1
<#
.SYNOPSIS
    Local, opt-in research helper: extracts a Cluster Windows installer
    (Inno Setup) with innounp, optionally disassembles the embedded
    PascalScript bytecode (CompiledCode.bin), and optionally downloads /
    extracts the client 7z package that the installer itself fetches.

.DESCRIPTION
    Everything this script produces stays in git-ignored directories
    (.work/, downloads/). Nothing is committed, published or uploaded.

    Steps (each subsequent step is opt-in via parameters):
      1. innounp -x -d<OutputDirectory> -y -m <InstallerPath>
         produces  {app}\, [Code]?\, embedded\CompiledCode.bin,
         install_script.iss  (layout names match what innounp emits).
      2. -Disassemble: builds/uses tools/ifpsdasm-wrapper (IFPSLib-based)
         and writes CompiledCode.dis.txt next to the bytecode.
      3. -DownloadClientPackage: reads the disassembly / install script and
         downloads the referenced https://update.cluster.mu/.../cluster_win.7z
         (an anonymous download, as the research confirmed) into
         downloads\cluster_win.7z. Skipped when the file already exists.
      4. -ExtractClientPackage: user-installed 7-Zip extracts the archive
         into .work\client-package (VuplexWebViewChromium excluded) so
         scripts/analyze-client.ps1 can inspect it.

    This script performs no authentication and never tampers with access
    controls; it reproduces only the plain-file fetch that the installer
    itself performs. The cluster client and any world content remain
    Cluster's creators' copyrighted property: keep them local.
#>
[CmdletBinding()]
param(
    # Path to the user-provided installer (Inno Setup bootstrap, e.g. cluster_win.exe).
    # Omit when reusing an already-extracted package; then only the 7z steps run.
    [string]$InstallerPath,

    [string]$OutputDirectory,

    [switch]$Disassemble,
    [string]$Configuration = 'Release',

    [switch]$DownloadClientPackage,
    [switch]$ExtractClientPackage,
    # Optional explicit path to a cluster_win.7z you already hold (default: downloads\cluster_win.7z)
    [string]$ClientPackageArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

try {
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
    Import-Module (Join-Path $repoRoot 'scripts' 'lib' 'ClusterMapDownloaderSupport.psm1') -Force -DisableNameChecking
    Initialize-Console
    $paths = Get-RepoPaths -RepoRoot $repoRoot
    if (-not $OutputDirectory) { $OutputDirectory = $paths.InstallerDir }
    $outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

    Write-SupportInfo ('extract-installer — target: ' + $outputRoot)

    $compiledCodeBin  = $null
    $disassemblyPath  = $null

    # --- step 1: innounp extraction -------------------------------------
    if ($InstallerPath) {
        if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
            throw "Installer not found: '$InstallerPath' (download it yourself; this project never fetches the installer)."
        }
        $innounpExe = Confirm-InnounpTool
        Write-Step ("innounp -> extract {0}" -f (Split-Path -Leaf $InstallerPath))
        # innounp option layout (verified locally): innounp [command] [options] <setup.exe>
        #   -x        extract everything (requires a decompressor supporting embedded files)
        #   -d<DIR>   output directory
        #   -y        assume "yes" on all prompts (overwrite)
        #   -m        process internal embedded files (required to reach embedded\CompiledCode.bin)
        # Switch tokens are passed as separate quoted arguments on purpose.
        & $innounpExe -x -y -m "-d$outputRoot" $InstallerPath
        if ($LASTEXITCODE -ne 0) {
            throw "innounp failed with exit code $LASTEXITCODE while extracting '$InstallerPath'"
        }
        Write-StepOk "extracted into $outputRoot"

        $compiledCodeBin = Join-Path $outputRoot 'embedded\CompiledCode.bin'
        if (-not (Test-Path -LiteralPath $compiledCodeBin)) {
            $found = Get-ChildItem -LiteralPath $outputRoot -Recurse -Filter 'CompiledCode.bin' |
                Select-Object -First 1
            if ($found) { $compiledCodeBin = $found.FullName }
        }
        if ($compiledCodeBin -and (Test-Path -LiteralPath $compiledCodeBin -PathType Leaf)) {
            Write-StepOk ("embedded bytecode present: {0}" -f $compiledCodeBin)
        }
        else {
            Write-StepWarn ('embedded\CompiledCode.bin was not found in the extraction; the ' +
                '"-m" flow may differ for this installer version. Listing the top entries:')
            Get-ChildItem -LiteralPath $outputRoot | ForEach-Object { Write-SupportInfo ("  - {0}" -f $_.Name) }
        }
    }

    # --- step 2: (-Disassemble) PascalScript bytecode -> text ------------
    if ($Disassemble) {
        if (-not $compiledCodeBin -or -not (Test-Path -LiteralPath $compiledCodeBin -PathType Leaf)) {
            throw "-Disassemble requested but no CompiledCode.bin is available (extract an installer first)."
        }
        Confirm-IfpsDasmWrapper -Configuration $Configuration | Out-Null
        Write-Step ('ifpsdasm-wrapper -> disassemble {0}' -f $compiledCodeBin)
        & dotnet run --project $paths.WrapperProject -c $Configuration -- $compiledCodeBin
        if ($LASTEXITCODE -ne 0) {
            throw "ifpsdasm-wrapper failed with exit code $LASTEXITCODE"
        }
        $disassemblyPath = [IO.Path]::ChangeExtension($compiledCodeBin, '.dis.txt')
        if (-not (Test-Path -LiteralPath $disassemblyPath)) {
            throw "disassembly output was not created: $disassemblyPath"
        }
        Write-StepOk ("disassembly written: {0}" -f $disassemblyPath)
        Write-SupportInfo ('Next: grep the disassembly for https / cluster_win / cluster.7z / ' +
            'installer URLs (scripts/analyze-client.ps1 works on client packages too).')
    }

    # --- step 3: (-DownloadClientPackage) anonymous client package fetch --
    if ($DownloadClientPackage) {
        $archiveTarget = Join-Path $paths.DownloadsDir 'cluster_win.7z'
        if (Test-Path -LiteralPath $archiveTarget -PathType Leaf) {
            Write-StepWarn "already present, skipping download: $archiveTarget"
        }
        else {
            $packageUrl = $null
            foreach ($candidate in @($disassemblyPath, (Join-Path $outputRoot 'install_script.iss'),
                                     (Join-Path $outputRoot 'embedded\CompiledCode.dis.txt'))) {
                if (-not ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf))) { continue }
                $content = Get-Content -LiteralPath $candidate -Raw
                $discovered = [regex]::Matches(
                    $content,
                    'https://update\.cluster\.mu/[^\s"''\\)]*?\.7z') |
                    Select-Object -ExpandProperty Value -Unique
                if ($discovered.Count -gt 0) { $packageUrl = ($discovered | Select-Object -First 1); break }
            }
            if (-not $packageUrl) {
                throw ('Could not discover the client package URL from the disassembly or install script. ' +
                    'Run with -Disassemble first, or re-download the installer.' +
                    ' (The URL is embedded in CompiledCode.bin; it is not hardcoded anywhere in this repository.)')
            }
            # For documentation the URL is shown with its opaque per-release path segment,
            # which the installer itself embeds; it is not a credential and grants no access.
            Write-Step ("discovered client package URL (per-release path): " + $packageUrl)
            Invoke-CurlDownload -Url ([Uri]$packageUrl) -Destination $archiveTarget `
                -Description 'cluster_win.7z (anonymous download; single file, no auth)' | Out-Null
        }
    }

    # --- step 4: (-ExtractClientPackage) 7-Zip extraction ------------------
    if ($ExtractClientPackage) {
        $archive = $ClientPackageArchive
        if (-not $archive) { $archive = Join-Path $paths.DownloadsDir 'cluster_win.7z' }
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
            throw "-ExtractClientPackage requested but the archive was not found: $archive " +
                "(use -DownloadClientPackage first, or pass -ClientPackageArchive <path>)"
        }
        $sevenZip = Find-SevenZip
        if (-not $sevenZip) {
            throw ('7-Zip was not found on PATH or in Program Files. Install it from ' +
                'https://www.7-zip.org/ and re-run (it is a user-installed tool; this project never fetches it).')
        }
        Write-Step ("7z x -> {0}" -f $paths.ClientPackageDir)
        New-Item -ItemType Directory -Force -Path $paths.ClientPackageDir | Out-Null
        # Separate quoted switch tokens (7-Zip switch parsing showed batching fails):
        #   x            extract with full paths
        #   -y           assume Yes on every prompt
        #   -o<DIR>      output directory (no space: one token)
        #   -xr!<name>   recursively exclude the Chromium webview files
        & $sevenZip x -y "-o$($paths.ClientPackageDir)" "-xr!VuplexWebViewChromium" $archive
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip failed with exit code $LASTEXITCODE while extracting $archive"
        }
        Write-StepOk ("client package extracted: {0}" -f $paths.ClientPackageDir)
        Write-SupportInfo ('Next: inspect the client package, e.g.:')
        Write-SupportInfo ('  powershell -File {0} -ClientDirectory "{1}" -Il2CppDump' `
            -f (Join-Path $paths.ScriptsDir 'analyze-client.ps1'), $paths.ClientPackageDir)
    }

    Write-SupportSuccess 'extract-installer completed.'
    exit 0
}
catch {
    Write-StepFail ("extract-installer failed: {0}" -f $_.Exception.Message)
    exit 1
}
