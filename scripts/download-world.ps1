#Requires -Version 5.1
<#
.SYNOPSIS
    The main entry point: downloads publicly accessible cluster.mu world
    data via the cluster-map-downloader CLI (building it first when needed).

.DESCRIPTION
    Pipeline (thin wrapper around artifacts-local/cli/cluster-map-downloader.exe):

        .\scripts\download-world.ps1 -WorldUrl "https://cluster.mu/w/<WORLD_ID>" -Output ".\output"

    Exit codes (from the CLI, mirrored by this script):
      0 success; 1 error; 2 world asset download not implemented yet;
      3 usage error; 4 world is not publicly accessible;
      5 delivery requires authorization the public client flow does not provide.

    When the CLI exits with 2, this script also makes sure the exact sentence
    "World asset download is not implemented yet." has been printed.

    The tool fetches only publicly accessible metadata (GET /v1/worlds/{id})
    without authentication by default. The explicit opt-in switch -GuestEntry
    additionally runs the documented guest-entry mode (anonymous Firebase
    sign-in, entry request, room-enter v2, then the venue asset downloads) —
    see docs/RESEARCH.md §8. Keys/tokens are never printed or persisted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorldUrl,

    [string]$Output = (Join-Path (Get-Location) 'output'),

    [switch]$MetadataOnly,
    [switch]$DryRun,
    [switch]$Json,
    [switch]$Rebuild,

    # Explicit opt-in guest-entry mode (off by default; never combined with -MetadataOnly).
    [switch]$GuestEntry,
    # Firebase *web* API key (a public client-shipped identifier, not a secret).
    # Alternatives: -ClientPackage / -Il2CppDump for local auto-detection.
    [string]$FirebaseApiKey,
    [string]$ClientPackage,
    [string]$Il2CppDump,

    [string]$AppVersion,
    [string]$BuildVersion,
    [string]$Device,
    [string]$Platform,
    [string]$ApiBase,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

try {
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
    Import-Module (Join-Path $repoRoot 'scripts' 'lib' 'ClusterMapDownloaderSupport.psm1') -Force -DisableNameChecking
    Initialize-Console
    $paths = Get-RepoPaths -RepoRoot $repoRoot

    # --- ensure the CLI is published ------------------------------------
    $cliExe = Join-Path $paths.CliPublishDir 'cluster-map-downloader.exe'
    $needPublish = ($Rebuild -or -not (Test-Path -LiteralPath $cliExe -PathType Leaf))
    if ($needPublish) {
        $null = Assert-DotNetAvailable
        if (-not $Rebuild) {
            Write-StepWarn "CLI not found at $cliExe — publishing it once now."
        }
        Write-Step ('dotnet publish {0} -c {1} -> {2}' -f $paths.CliProject, $Configuration, $paths.CliPublishDir)
        & dotnet publish $paths.CliProject -c $Configuration --nologo -v minimal -o $paths.CliPublishDir
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }
        Write-StepOk "CLI published: $cliExe"
    }

    # --- build the CLI command line -------------------------------------
    if ($GuestEntry -and $MetadataOnly) {
        throw '-GuestEntry cannot be combined with -MetadataOnly (guest entry exists to fetch the venue asset manifest).'
    }

    $command = if ($MetadataOnly) { 'metadata' } else { 'download' }
    $cliArguments = @($command, $WorldUrl, '-o', $Output)
    if ($DryRun)          { $cliArguments += '--dry-run' }
    if ($Json)            { $cliArguments += '--json' }
    if ($AppVersion)      { $cliArguments += @('--app-version', $AppVersion) }
    if ($BuildVersion)    { $cliArguments += @('--build-version', $BuildVersion) }
    if ($Device)          { $cliArguments += @('--device', $Device) }
    if ($Platform)        { $cliArguments += @('--platform', $Platform) }
    if ($ApiBase)         { $cliArguments += @('--api-base', $ApiBase) }
    if ($GuestEntry)      { $cliArguments += '--guest-entry' }
    if ($FirebaseApiKey)  { $cliArguments += @('--firebase-api-key', $FirebaseApiKey) }
    if ($ClientPackage)   { $cliArguments += @('--client-package', $ClientPackage) }
    if ($Il2CppDump)      { $cliArguments += @('--il2cpp-dump', $Il2CppDump) }

    # Never echo a user-supplied API key into the console/log.
    $printed = @($cliArguments)
    for ($i = 0; $i -lt $printed.Count; $i++) {
        if ($printed[$i] -eq '--firebase-api-key' -and $i -lt ($printed.Count - 1)) {
            $printed[$i + 1] = '<masked>'
        }
    }
    Write-SupportInfo ('cluster-map-downloader ' + ($printed -join ' '))
    & $cliExe @cliArguments
    $exitCode = $LASTEXITCODE

    switch ($exitCode) {
        0 { }
        1 { Write-StepWarn 'the run failed (exit code 1).' }
        2 {
            # Pipeline guarantee: the not-implemented sentence is always printed.
            Write-Host ''
            Write-Host 'World asset download is not implemented yet.' -ForegroundColor Yellow
        }
        3 { Write-StepWarn 'usage error (exit code 3): check the parameters.' }
        4 { Write-StepWarn 'world is not publicly accessible (exit code 4).' }
        5 { Write-StepWarn 'guest-entry delivery refused by cluster (exit code 5); beyond the guest boundary this tool does not go.' }
        default { Write-StepWarn ("unexpected exit code {0}" -f $exitCode) }
    }

    exit $exitCode
}
catch {
    Write-StepFail ("download-world failed: {0}" -f $_.Exception.Message)
    exit 1
}
