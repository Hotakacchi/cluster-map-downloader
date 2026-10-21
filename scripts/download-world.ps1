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
    and never emulates authentication: see docs/RESEARCH.md for why the
    delivery stage deliberately stops at that boundary.
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
    $command = if ($MetadataOnly) { 'metadata' } else { 'download' }
    $cliArguments = @($command, $WorldUrl, '-o', $Output)
    if ($DryRun)          { $cliArguments += '--dry-run' }
    if ($Json)            { $cliArguments += '--json' }
    if ($AppVersion)      { $cliArguments += @('--app-version', $AppVersion) }
    if ($BuildVersion)    { $cliArguments += @('--build-version', $BuildVersion) }
    if ($Device)          { $cliArguments += @('--device', $Device) }
    if ($Platform)        { $cliArguments += @('--platform', $Platform) }
    if ($ApiBase)         { $cliArguments += @('--api-base', $ApiBase) }

    Write-SupportInfo ('cluster-map-downloader ' + ($cliArguments -join ' '))
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
        5 { Write-StepWarn 'delivery requires non-public authorization (exit code 5); the tool does not bypass.' }
        default { Write-StepWarn ("unexpected exit code {0}" -f $exitCode) }
    }

    exit $exitCode
}
catch {
    Write-StepFail ("download-world failed: {0}" -f $_.Exception.Message)
    exit 1
}
