#Requires -Version 5.1
<#
.SYNOPSIS
    Bootstraps the cluster-map-downloader project: builds/publishes the CLI,
    optionally runs the test suite, and (opt-in) fetches the third-party
    research tools into the ignored directory .work/third-party/.

.DESCRIPTION
    Default flow (no parameters):
      1. check the .NET SDK,
      2. restore + build the solution (src/lib, src/CLI, tests),
      3. publish the CLI to artifacts-local/cli and the GUI to
         artifacts-local/gui (both ignored),
      4. print where to run things from.

    Opt-in switches:
      -RunTests          run the xUnit suite (after the build),
      -WithTools         innounp 2.x (GPL, fetched at runtime) +
                         IFPSTools.NET (MIT, cloned & patched) +
                         tools/ifpsdasm-wrapper build,
      -WithAnalysisTools Il2CppDumper v6.7.46 net6 (MIT),
      -Force             re-fetch/overwrite third-party tools,
      -SkipCliPublish    build but skip the publish step.

    Third-party tools are never vendored into the repository: everything is
    downloaded/cloned into .work/third-party (git-ignored). This script and
    this repository contain no client binaries, no world assets, no keys.
#>
[CmdletBinding()]
param(
    [switch]$RunTests,
    [switch]$WithTools,
    [switch]$WithAnalysisTools,
    [switch]$Force,
    [switch]$SkipCliPublish,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

try {
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
    Import-Module (Join-Path $repoRoot 'scripts' 'lib' 'ClusterMapDownloaderSupport.psm1') -Force -DisableNameChecking
    Initialize-Console
    $paths = Get-RepoPaths -RepoRoot $repoRoot

    Write-SupportInfo ('cluster-map-downloader bootstrap — {0}' -f $repoRoot)

    # --- 1. .NET SDK ----------------------------------------------------
    Write-Step 'Checking the .NET SDK'
    $sdkVersion = Assert-DotNetAvailable
    Write-StepOk ".NET SDK found: $sdkVersion"

    # --- 2. restore + build ----------------------------------------------
    Write-Step ('Building {0} (-c {1})' -f $paths.CliSolution, $Configuration)
    & dotnet build $paths.CliSolution -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "solution build failed with exit code $LASTEXITCODE" }
    Write-StepOk 'Solution built'

    # --- 3. publish the CLI + GUI -----------------------------------------
    $cliExe = Join-Path $paths.CliPublishDir 'cluster-map-downloader.exe'
    if (-not $SkipCliPublish) {
        Write-Step ('Publishing CLI to {0}' -f $paths.CliPublishDir)
        & dotnet publish $paths.CliProject -c $Configuration --nologo -v minimal -o $paths.CliPublishDir
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }
        Write-StepOk "CLI published: $cliExe"

        Write-Step ('Publishing GUI to {0}' -f $paths.GuiPublishDir)
        & dotnet publish $paths.GuiProject -c $Configuration --nologo -v minimal -o $paths.GuiPublishDir
        if ($LASTEXITCODE -ne 0) { throw "GUI publish failed with exit code $LASTEXITCODE" }
        Write-StepOk ("GUI published: {0}" -f (Join-Path $paths.GuiPublishDir 'cluster-map-downloader-gui.exe'))
    }

    # --- 4. (-WithTools / -WithAnalysisTools) third-party tools ----------
    if ($WithTools) {
        Write-SupportInfo '--- research tools (-WithTools) ---'
        $innounpExe = Confirm-InnounpTool -Force:$Force
        Write-StepOk "innounp available: $innounpExe (GNU GPL; fetched at runtime, never vendored)"
        $ifps = Confirm-IfpsToolsSource -Force:$Force
        Write-StepOk ("IFPSTools.NET available: {0} (MIT; cloned, IFPSLib.csproj dependency-patched)" -f $ifps.RepoDir)
        Confirm-IfpsDasmWrapper -Configuration $Configuration | Out-Null
        Write-StepOk ('ifpsdasm-wrapper built (dotnet build {0})' -f $paths.WrapperProject)
    }

    if ($WithAnalysisTools) {
        Write-SupportInfo '--- analysis tools (-WithAnalysisTools) ---'
        $ilmDir = Confirm-Il2CppDumperTool -Force:$Force
        Write-StepOk "Il2CppDumper available: $ilmDir (MIT; fetched at runtime, never vendored)"
        $sevenZip = Find-SevenZip
        if (-not $sevenZip) {
            Write-StepWarn ('7-Zip was not found (user-installed tool, never fetched). Needed ' +
                'to extract .7z client packages; install it from https://www.7-zip.org/')
        }
    }

    # --- 5. (-RunTests) xUnit --------------------------------------------
    if ($RunTests) {
        Write-SupportInfo '--- test suite (-RunTests) ---'
        & dotnet test $paths.CliSolution -c $Configuration --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw "test run failed with exit code $LASTEXITCODE" }
        Write-StepOk 'Test suite passed'
    }

    # --- 6. summary -------------------------------------------------------
    Write-SupportSuccess 'Bootstrap complete.'
    if (-not $SkipCliPublish -and (Test-Path -LiteralPath $cliExe)) {
        Write-SupportInfo  ''
        Write-SupportInfo  'Quick start:'
        Write-SupportInfo  ("  {0} version" -f $cliExe)
        Write-SupportInfo  ('  powershell -File {0} -WorldUrl "https://cluster.mu/w/<WORLD_ID>" -Output .\output' `
            -f (Join-Path $paths.ScriptsDir 'download-world.ps1'))
    }
    if ($WithTools) {
        Write-SupportInfo ''
        Write-SupportInfo 'Installer research (local analysis only; opt-in):'
        Write-SupportInfo ('  powershell -File {0} -InstallerPath "C:\path\to\cluster_win.exe" -Disassemble' `
            -f (Join-Path $paths.ScriptsDir 'extract-installer.ps1'))
    }
    exit 0
}
catch {
    if (Get-Command Write-StepFail -ErrorAction SilentlyContinue) {
        Write-StepFail ("bootstrap failed: {0}" -f $_.Exception.Message)
    }
    else {
        Write-Error $_
    }
    exit 1
}
