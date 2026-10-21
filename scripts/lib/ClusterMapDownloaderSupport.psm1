#Requires -Version 5.1
<#
.SYNOPSIS
    Shared helpers for the cluster-map-downloader PowerShell research scripts.

.DESCRIPTION
    Everything in this module is local-first and opt-in:

    * third-party tools are fetched into `.work/third-party/` (ignored) on demand,
    * binary/string scans run chunked so the ~180 MiB client files stay within
      reasonable memory bounds,
    * every textual output passes through secret-shape masking so that public
      API key / access-token value classes never land in reports.

    The module performs no authentication and never contacts authenticated
    cluster endpoints.
#>

Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

function Resolve-RepoRoot {
    <# Repository root: scripts/lib/.. ; trailing backslash removed.#>
    [CmdletBinding()]
    param()
    $root = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')
    return $root.ProviderPath.TrimEnd('\')
}

function Get-RepoPaths {
    <#Standard layout record used by every script.#>
    [CmdletBinding()]
    param([string]$RepoRoot)
    if (-not $RepoRoot) { $RepoRoot = Resolve-RepoRoot }
    $work = Join-Path $RepoRoot '.work'
    return [pscustomobject]@{
        RepoRoot        = $RepoRoot
        ScriptsDir      = Join-Path $RepoRoot 'scripts'
        LibDir          = Join-Path $RepoRoot 'scripts' 'lib'
        WorkRoot        = $work
        ThirdPartyRoot  = Join-Path $work 'third-party'
        Il2CppDumpDir   = Join-Path $work 'il2cpp-dump'
        ClientPackageDir= Join-Path $work 'client-package'
        InstallerDir    = Join-Path $work 'installer'
        AnalysisDir     = Join-Path $work 'analysis'
        TempRoot        = Join-Path $RepoRoot '.temp'
        DownloadsDir    = Join-Path $RepoRoot 'downloads'
        CliPublishDir   = Join-Path $RepoRoot 'artifacts-local' 'cli'
        CliProject      = Join-Path $RepoRoot 'src' 'ClusterMapDownloader.Cli' 'ClusterMapDownloader.Cli.csproj'
        CliSolution     = Join-Path $RepoRoot 'ClusterMapDownloader.sln'
        WrapperProject  = Join-Path $RepoRoot 'tools' 'ifpsdasm-wrapper' 'ifpsdasm-wrapper.csproj'
        WrapperSolution = Join-Path $RepoRoot 'tools' 'ifpsdasm-wrapper' 'ifpsdasm-wrapper.sln'
    }
}

# ---------------------------------------------------------------------------
# Console helpers
# ---------------------------------------------------------------------------

function Initialize-Console {
    <#Best-effort UTF-8 console; harmless inside non-interactive hosts.#>
    [CmdletBinding()]
    param()
    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
}

function Write-SupportInfo    { param([string]$Message) Write-Host $Message -ForegroundColor Cyan }
function Write-SupportSuccess { param([string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-SupportWarn    { param([string]$Message) Write-Host $Message -ForegroundColor Yellow }
function Write-SupportError   { param([string]$Message) Write-Host $Message -ForegroundColor Magenta }

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("[ ] {0}" -f $Message) -ForegroundColor Cyan
}
function Write-StepOk {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("[ok]  {0}" -f $Message) -ForegroundColor Green
}
function Write-StepWarn {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("[warn] {0}" -f $Message) -ForegroundColor Yellow
}
function Write-StepFail {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("[fail] {0}" -f $Message) -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# Environment checks
# ---------------------------------------------------------------------------

function Assert-DotNetAvailable {
    <#Throws when no dotnet SDK is usable; returns the SDK version string.#>
    [CmdletBinding()]
    param()
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "The .NET SDK is required (https://dotnet.microsoft.com/download) but 'dotnet' is not on PATH."
    }
    $version = & dotnet --version 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $version) {
        throw "'dotnet' exists but 'dotnet --version' failed. Install the .NET SDK 8.0 or newer."
    }
    return ($version | Select-Object -First 1).ToString().Trim()
}

function Assert-GitAvailable {
    <#Throws when no git client is usable; returns the git version line.#>
    [CmdletBinding()]
    param()
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        throw "A 'git' client is required for cloning third-party sources but is not on PATH."
    }
    return (& git --version).ToString().Trim()
}

function Find-SevenZip {
    <#Locates a user-installed 7-Zip console executable; $null when absent. Never fetched.#>
    [CmdletBinding()]
    param()
    $candidates = @(
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    $choco7z = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if ($choco7z) { return $choco7z }
    return $null
}

# ---------------------------------------------------------------------------
# Downloads / archives
# ---------------------------------------------------------------------------

function Invoke-CurlDownload {
    <#
        Downloads $Url to $Destination using curl.exe (preferred) or Invoke-WebRequest.
        Writes to '$Destination.part' and renames on success. Skips when the target file
        already exists non-empty (unless -Force). Returns the final file path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Uri]$Url,
        [Parameter(Mandatory)][string]$Destination,
        [string]$Description = 'download',
        [switch]$Force
    )
    $destDir = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    if (-not $Force -and (Test-Path -LiteralPath $Destination) -and
        (Get-Item -LiteralPath $Destination).Length -gt 0 -and $Destination -notlike '*.part') {
        Write-StepWarn ("{0} already present: {1}" -f $Description, $Destination)
        return $Destination
    }

    $part = "$Destination.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }

    # Plain product token; no parentheses (some HTTP clients split comment sections).
    $ua = 'cluster-map-downloader-bootstrap/1.0'
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        Write-Step ("curl -L -o {0} {1}" -f $part, $Url.AbsoluteUri)
        # Each switch is its own quoted token (7-Zip/curl switch parsing showed batching fails).
        & $curl.Source -L -f -sS -o $part -A $ua $Url.AbsoluteUri
        if ($LASTEXITCODE -ne 0) {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
            throw "curl.exe failed (exit code $LASTEXITCODE) while fetching $Url"
        }
    }
    else {
        Write-StepWarn "curl.exe not found; falling back to Invoke-WebRequest"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -UserAgent $ua -OutFile $part -UseBasicParsing
    }

    $size = (Get-Item -LiteralPath $part).Length
    Move-Item -LiteralPath $part -Destination $Destination -Force
    Write-StepOk ("{0} saved: {1} ({2})" -f $Description, $Destination, (Format-ByteSize $size))
    return $Destination
}

function Expand-ZipArchiveToDirectory {
    <#.NET ZipFile extraction that behaves the same on PowerShell 5.1 and 7.#>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Zip, [Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Destination)
}

function Format-ByteSize {
    <# "1.2 MiB"-style formatting shared across reports.#>
    [CmdletBinding()]
    param([Parameter(Mandatory)][long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GiB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MiB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KiB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

# ---------------------------------------------------------------------------
# Third-party tool fetchers (all land in .work/third-party, never vendored)
# ---------------------------------------------------------------------------

$script:ThirdPartyTools = @{
    # innounp 2.x: jrathlev's maintainer build; GNU GPL. Fetched at runtime,
    # never committed to this repository.
    innounp = [pscustomobject]@{
        Version    = '2.x (jrathlev, ui_2_2_13)'
        License    = 'GNU GPL'
        Url        = 'https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_13/innounp-2.zip'
        LicenseUrl = 'https://github.com/jrathlev/InnoUnpacker-Windows-GUI#license'
    }
    # Il2CppDumper (Perfare): MIT. Research session used the net6 build via dotnet.
    il2cppdumper = [pscustomobject]@{
        Version    = 'v6.7.46 (net6)'
        License    = 'MIT'
        Url        = 'https://github.com/Perfare/Il2CppDumper/releases/download/v6.7.46/Il2CppDumper-net6-v6.7.46.zip'
        LicenseUrl = 'https://github.com/Perfare/Il2CppDumper/blob/v6.7.46/LICENSE'
    }
    # IFPSTools.NET (Wack0): MIT, cloned from upstream at bootstrap time.
    ifpstools = [pscustomobject]@{
        Version    = 'main (cloned by scripts/bootstrap.ps1 -WithTools)'
        License    = 'MIT'
        Url        = 'https://github.com/Wack0/IFPSTools.NET.git'
        LicenseUrl = 'https://github.com/Wack0/IFPSTools.NET#license'
    }
}

function Get-ThirdPartyToolInfo {
    param([Parameter(Mandatory)][ValidateSet('innounp', 'il2cppdumper', 'ifpstools')][string]$Name)
    return $script:ThirdPartyTools[$Name]
}

function Get-ToolDirectory {
    param([Parameter(Mandatory)][ValidateSet('innounp', 'il2cppdumper', 'ifpstools')][string]$Name)
    $directoryNames = @{
        innounp      = 'innounp'
        il2cppdumper = 'Il2CppDumper'
        ifpstools    = 'IFPSTools.NET'
    }
    $paths = Get-RepoPaths
    return (Join-Path $paths.ThirdPartyRoot $directoryNames[$Name])
}

function Confirm-InnounpTool {
    <#Returns the innounp.exe path, fetching + expanding into .work/third-party when needed.#>
    [CmdletBinding()]
    param([switch]$Force)
    $paths = Get-RepoPaths
    $toolDir = Get-ToolDirectory innounp
    $exe = Join-Path $toolDir 'innounp.exe'
    if (-not $Force -and (Test-Path -LiteralPath $exe)) { return $exe }
    $toolInfo = Get-ThirdPartyToolInfo innounp
    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    $zip = Join-Path $paths.ThirdPartyRoot 'innounp-2.zip'
    Invoke-CurlDownload -Url ([Uri]$toolInfo.Url) -Destination $zip `
        -Description 'innounp 2.x (GPL; fetched at runtime, never vendored)' -Force:$Force
    Expand-ZipArchiveToDirectory -Zip $zip -Destination $toolDir
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "innounp.exe was not found after expanding $($toolInfo.Url) into $toolDir"
    }
    return $exe
}

function Confirm-Il2CppDumperTool {
    <#
        Returns the Il2CppDumper directory, fetching when needed. Also pins
        config.json's RequireAnyKey to $false so batch flows never stall on
        "press any key".
    #>
    [CmdletBinding()]
    param([switch]$Force)
    $toolDir = Get-ToolDirectory il2cppdumper
    $dll = Join-Path $toolDir 'Il2CppDumper.dll'
    if (-not $Force -and (Test-Path -LiteralPath $dll)) { return $toolDir }
    $toolInfo = Get-ThirdPartyToolInfo il2cppdumper
    if ($Force -and (Test-Path -LiteralPath $toolDir)) { Remove-Item -LiteralPath $toolDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    $zip = Join-Path $toolDir 'Il2CppDumper-net6.zip'
    Invoke-CurlDownload -Url ([Uri]$toolInfo.Url) -Destination $zip `
        -Description 'Il2CppDumper net6 zip (MIT; fetched at runtime)' -Force:$Force
    Expand-ZipArchiveToDirectory -Zip $zip -Destination $toolDir
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "Il2CppDumper.dll was not found after expanding $($toolInfo.Url) into $toolDir"
    }
    Set-Il2CppDumperBatchConfig -ConfigPath (Join-Path $toolDir 'config.json')
    return $toolDir
}

function Set-Il2CppDumperBatchConfig {
    <#Pins "RequireAnyKey": false so unattended Il2CppDumper runs do not wait for a key press.#>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ConfigPath)
    if (-not (Test-Path -LiteralPath $ConfigPath)) { return }
    try {
        $raw = Get-Content -LiteralPath $ConfigPath -Raw
        $json = $raw | ConvertFrom-Json
        if ($json.RequireAnyKey -eq $false) { return }
        $json.RequireAnyKey = $false
        $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
        Write-StepOk 'Il2CppDumper config.json: RequireAnyKey = false'
    }
    catch {
        Write-StepWarn ("Could not patch Il2CppDumper config.json automatically: {0}. " +
            "If a run pauses, edit config.json manually and set ""RequireAnyKey"": false." -f $_.Exception.Message)
    }
}

function Confirm-IfpsToolsSource {
    <#
        Clones IFPSTools.NET into .work/third-party/IFPSTools.NET and applies the
        NU1605 dependency patch (System.Runtime.CompilerServices.Unsafe 4.5.3 -> 6.0.0).
    #>
    [CmdletBinding()]
    param([switch]$Force)
    $repoDir = Get-ToolDirectory ifpstools
    $libCsproj = Join-Path $repoDir 'IFPSLib\IFPSLib.csproj'
    if (-not $Force -and (Test-Path -LiteralPath $libCsproj)) {
        return [pscustomobject]@{ RepoDir = $repoDir; LibCsproj = $libCsproj; ClonedNow = $false }
    }
    $null = Assert-GitAvailable
    $toolInfo = Get-ThirdPartyToolInfo ifpstools
    if ($Force -and (Test-Path -LiteralPath $repoDir)) { Remove-Item -LiteralPath $repoDir -Recurse -Force }
    Write-Step ("git clone --depth 1 {0} -> {1}" -f $toolInfo.Url, $repoDir)
    & git clone --depth 1 $toolInfo.Url $repoDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed with exit code $LASTEXITCODE for $($toolInfo.Url)" }
    Write-StepOk "IFPSTools.NET cloned into $repoDir"
    Repair-IfpsLibCsprojPath -CsprojPath $libCsproj
    return [pscustomobject]@{ RepoDir = $repoDir; LibCsproj = $libCsproj; ClonedNow = $true }
}

function Repair-IfpsLibCsprojPath {
    <#
        Upstream targets System.Runtime.CompilerServices.Unsafe 4.5.3, which produces
        NU1605 downgrade errors on the net8.0 toolchain. Research-verified fix: 6.0.0.
        Replaces only that one package reference; no-op when already patched.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CsprojPath)
    if (-not (Test-Path -LiteralPath $CsprojPath)) {
        throw "IFPSLib project not found at '$CsprojPath'. The clone may have failed."
    }
    $content = Get-Content -LiteralPath $CsprojPath -Raw
    $patched = $content -replace
        '(<PackageReference\s+Include\s*=\s*"System\.Runtime\.CompilerServices\.Unsafe"\s+Version\s*=\s*")4\.5\.3("\s*/>)',
        '${1}6.0.0${2}'
    if ($patched -eq $content) {
        if ($content -like '*System.Runtime.CompilerServices.Unsafe*6.0.0*') {
            Write-StepOk 'IFPSLib.csproj already patched (System.Runtime.CompilerServices.Unsafe 6.0.0).'
        }
        else {
            Write-StepWarn ("Could not find the System.Runtime.CompilerServices.Unsafe 4.5.3 reference in " +
                "$CsprojPath; if the build fails with NU1605, patch it to 6.0.0 manually.")
        }
        return
    }
    Set-Content -LiteralPath $CsprojPath -Value $patched -Encoding UTF8
    Write-StepOk 'IFPSLib.csproj patched: System.Runtime.CompilerServices.Unsafe 4.5.3 -> 6.0.0 (NU1605 fix).'
}

function Confirm-IfpsDasmWrapper {
    <#Builds tools/ifpsdasm-wrapper, which links against the cloned IFPSLib project.#>
    [CmdletBinding()]
    param([string]$Configuration = 'Release')
    $null = Assert-DotNetAvailable
    $null = Confirm-IfpsToolsSource
    $paths = Get-RepoPaths
    Write-Step ("dotnet build {0} -c {1}" -f $paths.WrapperProject, $Configuration)
    & dotnet build $paths.WrapperProject -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "ifpsdasm-wrapper build failed with exit code $LASTEXITCODE" }
    Write-StepOk 'ifpsdasm-wrapper built'
    return $paths.WrapperProject
}

# ---------------------------------------------------------------------------
# Secret masking (value classes only, never literals)
# ---------------------------------------------------------------------------

$script:SecretMaskTokens = @(
    @{ Pattern = 'AIza[0-9A-Za-z_-]{10,}'                  ; Label = 'AIza<REDACTED:google-key-class>' }
    @{ Pattern = 'pk_(?:live|test)_[0-9A-Za-z]{10,}'       ; Label = 'pk_*<REDACTED:stripe-key-class>' }
    @{ Pattern = 'sk_(?:live|test)_[0-9A-Za-z]{10,}'       ; Label = 'sk_*<REDACTED:stripe-key-class>' }
    @{ Pattern = 'AKIA[0-9A-Z]{12,}'                       ; Label = 'AKIA<REDACTED:aws-access-key-class>' }
    @{ Pattern = 'ya29\.[0-9A-Za-z_-]{10,}'                ; Label = 'ya29.<REDACTED:oauth-access-token-class>' }
    @{ Pattern = 'ghp_[0-9A-Za-z]{20,}'                    ; Label = 'ghp_<REDACTED:github-token-class>' }
    @{ Pattern = 'github_pat_[0-9A-Za-z_]{20,}'            ; Label = 'github_pat_<REDACTED:github-token-class>' }
    @{ Pattern = 'xox[bpas]-[0-9A-Za-z-]{10,}'             ; Label = 'xox*<REDACTED:slack-token-class>' }
    @{
        Pattern = '(?i)([?&])(?:key|api_?key|token|access_?token|refresh_?token|client_?secret|password|secret|signature|sig)=[0-9A-Za-z%._+/=-]{8,}'
        Label   = '$1<REDACTED>'
    }
)

function Hide-SecretLikeValues {
    <#Masks well-known key/token value classes in a single line of text (docs-safe).#>
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Text)
    foreach ($mask in $script:SecretMaskTokens) {
        $Text = [regex]::Replace($Text, $mask.Pattern, $mask.Label)
    }
    return $Text
}

# ---------------------------------------------------------------------------
# Large-file scanning (chunked, bounded memory)
# ---------------------------------------------------------------------------

$script:Latin1Encoding = $null

function Get-Latin1Encoding {
    <#ISO-8859-1 without requiring the Windows-only code-page provider on .NET (pwsh).#>
    [CmdletBinding()]
    param()
    if ($script:Latin1Encoding) { return $script:Latin1Encoding }
    $latin1Member = [System.Text.Encoding].GetMember('Latin1')
    if ($null -ne $latin1Member -and $latin1Member.Count -gt 0) {
        $script:Latin1Encoding = [System.Text.Encoding]::Latin1
    }
    else {
        $script:Latin1Encoding = [System.Text.Encoding]::GetEncoding(28591)
    }
    return $script:Latin1Encoding
}

function Convert-BytesToAsciiSafeText {
    <#Renders raw bytes as single-line printable text (non-ASCII -> '.').#>
    [CmdletBinding()]
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $latin1 = Get-Latin1Encoding
    $text = $latin1.GetString($Bytes)
    $sb = [System.Text.StringBuilder]::new($text.Length)
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($c -ge 0x20 -and $c -le 0x7E) { $null = $sb.Append($c) } else { $null = $sb.Append('.') }
    }
    return $sb.ToString()
}

function Invoke-FileScan {
    <#
        Primary binary-scan entry: streams $Path in windows (ChunkSize fresh bytes plus the
        previous window's carry tail) and runs every regex in $Patterns per window, keeping

          * absolute 1-based file offsets,
          * +/- ContextBytes printable context (non-ASCII and control chars become '.'),
          * secret-masked output (Hide-SecretLikeValues),
          * per-pattern match caps so giant binaries cannot blow up the report.

        Sliding windows (carry = CarryBytes) make chunk-boundary matches safe. Returns an
        array of PSCustomObject: { Pattern, File, Offset, Match, Context }.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Patterns,
        [ValidateRange(1, 4096)][int]$ContextBytes = 80,
        [ValidateRange(1, 10000)][int]$MaxMatchesPerPattern = 40,
        [ValidateRange(65536, 16777216)][int]$ChunkSize = 4194304,
        [ValidateRange(1, 4096)][int]$CarryBytes = 256
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Scan input not found: $Path" }
    $latin1 = Get-Latin1Encoding
    $fileInfo = Get-Item -LiteralPath $Path

    $compiled = @{}
    foreach ($key in $Patterns.Keys) {
        $compiled[$key] = [System.Text.RegularExpressions.Regex]::new([string]$Patterns[$key])
    }
    $perPatternHit = @{}
    foreach ($key in $compiled.Keys) { $perPatternHit[$key] = 0 }
    # Nested record dictionary keyed by "pattern|absOffset": a later window may
    # re-find a boundary-spanning match with the full length; re-assign replaces
    # the truncated record with the complete one (write-last-wins).
    $records = @{}

    $buffer = [byte[]]::new($ChunkSize)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $position = 0L
        $carryText = ''
        $windowCount = 0
        while ($true) {
            $remaining = $fileInfo.Length - $position
            if ($remaining -le 0) { break }
            $freshCount = [int][Math]::Min([long]$ChunkSize, $remaining)
            $red = $stream.Read($buffer, 0, $freshCount)
            if ($red -ne $freshCount) { $freshCount = $red; if ($freshCount -le 0) { break } }

            $freshText = $latin1.GetString($buffer, 0, $freshCount)
            $windowText = $carryText + $freshText
            $carryLocal = $carryText.Length
            $windowBase = $position - [long]$carryLocal

            foreach ($name in $compiled.Keys) {
                if ($perPatternHit[$name] -ge $MaxMatchesPerPattern) { continue }
                foreach ($m in $compiled[$name].Matches($windowText)) {
                    # A match fully inside the carry was already recorded by the previous
                    # window. A match that reaches past the carry was previously only a
                    # truncated boundary copy, so it is recorded here (write-last-wins
                    # replaces the same (pattern, offset) record with the full-length copy).
                    if (($m.Index + $m.Length) -le $carryLocal -and $carryLocal -gt 0) { continue }
                    $absolute = $windowBase + $m.Index
                    if ($perPatternHit[$name] -ge $MaxMatchesPerPattern -and
                        -not $records.ContainsKey($name + "`0" + $absolute)) { continue }
                    $ctxStart = [Math]::Max(0, $m.Index - $ContextBytes)
                    $ctxEnd   = [Math]::Min($windowText.Length, $m.Index + $m.Length + $ContextBytes)
                    $context  = $windowText.Substring($ctxStart, $ctxEnd - $ctxStart)
                    $context  = $context -replace '[\x00-\x1F\x7F-\xFF]', '.'
                    $matchText = $m.Value -replace '[\x00-\x1F\x7F-\xFF]', '.'
                    if ($perPatternHit[$name] -ge $MaxMatchesPerPattern) { break }
                    $perPatternHit[$name] = $perPatternHit[$name] + 1
                    $records[$name + "`0" + $absolute] = [pscustomobject]@{
                        Pattern = $name
                        File    = $fileInfo.Name
                        Offset  = ($absolute + 1)
                        Match   = (Hide-SecretLikeValues $matchText)
                        Context = (Hide-SecretLikeValues $context)
                    }
                }
            }

            $position += [long]$freshCount
            $windowCount += 1

            # carry only the last CarryBytes *fresh* bytes to cover the boundary region
            $tailSize = [Math]::Min([int]$CarryBytes, $freshCount)
            if ($tailSize -gt 0) {
                $carryText = $freshText.Substring($freshCount - $tailSize)
            }
            else {
                $carryText = ''
            }
            if ($windowCount % 16 -eq 0) {
                Write-Verbose ("Invoke-FileScan {0}: {1}/{2} bytes" -f $fileInfo.Name, $position, $fileInfo.Length)
            }
        }
    }
    finally { $stream.Dispose() }

    return @($records.Values | Sort-Object -Property 'Pattern', 'Offset')
}

function Find-AsciiStringsInFile {
    <#
        'strings'-style extractor for raw binaries: printable ([\x20-\x7E]) runs of
        >= MinLength bytes, returned with 1-based absolute offsets in ascending order.
        Windows carry prevTail so runs spanning read boundaries are merged correctly,
        and touching duplicate ranges are merged into maximal runs before printing.
        Capped at MaxStrings runs to keep reports small.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateRange(4, 1024)][int]$MinLength = 6,
        [ValidateRange(1, 1000000)][long]$MaxStrings = 20000,
        [ValidateRange(65536, 16777216)][int]$ChunkSize = 4194304
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Scan input not found: $Path" }
    $latin1 = Get-Latin1Encoding
    $fileInfo = Get-Item -LiteralPath $Path
    $runRegex = [System.Text.RegularExpressions.Regex]::new('[\x20-\x7E]{' + $MinLength + ',}')
    $ranges = [System.Collections.Generic.List[object]]::new()

    $buffer = [byte[]]::new($ChunkSize)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $position = 0L
        $carryText = ''
        while ($true) {
            $remaining = $fileInfo.Length - $position
            if ($remaining -le 0) { break }
            $freshCount = [int][Math]::Min([long]$ChunkSize, $remaining)
            $red = $stream.Read($buffer, 0, $freshCount)
            if ($red -ne $freshCount) { $freshCount = $red; if ($freshCount -le 0) { break } }

            $freshText = $latin1.GetString($buffer, 0, $freshCount)
            $windowText = $carryText + $freshText
            $carryLocal = $carryText.Length
            $windowBase = $position - [long]$carryLocal

            foreach ($m in $runRegex.Matches($windowText)) {
                $endsInsideCarry = ($m.Index + $m.Length) -le $carryLocal
                if ($m.Index -lt $carryLocal -and $endsInsideCarry) { continue }  # duplicate run
                $ranges.Add([pscustomobject]@{
                    Start  = ($windowBase + $m.Index)
                    Length = $m.Length
                })
            }

            $position += [long]$freshCount
            $tailSize = [Math]::Min([int]($MinLength - 1), $freshCount)
            if ($tailSize -gt 0) {
                $carryText = $freshText.Substring($freshCount - $tailSize)
            }
            else {
                $carryText = ''
            }
        }
    }
    finally { $stream.Dispose() }

    if ($ranges.Count -eq 0) { return @() }

    # merge touching/overlapping ranges into maximal runs (input is sorted first:
    # a boundary-spanning run is recorded twice, once truncated, once complete,
    # and those two ranges only touch/overlap)
    $sortedRanges = @($ranges | Sort-Object -Property 'Start', 'Length')
    $merged = [System.Collections.Generic.List[object]]::new()
    $current = $sortedRanges[0]
    for ($i = 1; $i -lt $sortedRanges.Count; $i++) {
        $next = $sortedRanges[$i]
        $currentEnd = $current.Start + $current.Length
        $nextEnd = $next.Start + $next.Length
        if ($next.Start -le $currentEnd) {
            $newEnd = [Math]::Max($currentEnd, $nextEnd)
            $current = [pscustomobject]@{ Start = $current.Start; Length = ($newEnd - $current.Start) }
        }
        else {
            $merged.Add($current)
            $current = $next
        }
    }
    $merged.Add($current)

    $limit = [Math]::Min([long]$merged.Count, $MaxStrings)
    if ($merged.Count -gt $MaxStrings) {
        Write-StepWarn ("Find-AsciiStringsInFile: capped at {0} of {1} runs for '{2}'" -f
            $limit, $merged.Count, $fileInfo.Name)
    }

    $strings = [System.Collections.Generic.List[object]]::new()
    $fs = [IO.File]::OpenRead($Path)
    try {
        for ($i = 0; $i -lt $limit; $i++) {
            $range = $merged[$i]
            $null = $fs.Seek($range.Start, [IO.SeekOrigin]::Begin)
            $bytes = [byte[]]::new([int]$range.Length)
            $null = $fs.Read($bytes, 0, $bytes.Length)
            $text = $latin1.GetString($bytes)
            if ($text.Length -ge $MinLength) {
                $strings.Add([pscustomobject]@{
                    Offset = ($range.Start + 1)
                    Text   = $text
                })
            }
        }
    }
    finally { $fs.Dispose() }

    return @($strings)
}

function Find-TextLineMatches {
    <#
        Streams a (large) text file line-by-line and collects pattern hits with line
        numbers and secret-masked excerpts. Used for text dumps such as IL2CPP dump.cs
        and Inno Setup disassembly output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Patterns,
        [ValidateRange(1, 10000)][int]$MaxPerPattern = 60,
        [ValidateRange(64, 2000)][int]$MaxLineLengthShown = 320
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Scan input not found: $Path" }

    $compiled = @{}
    foreach ($key in $Patterns.Keys) {
        $compiled[$key] = [System.Text.RegularExpressions.Regex]::new([string]$Patterns[$key])
    }
    $hits = @{}
    foreach ($key in $compiled.Keys) { $hits[$key] = 0 }
    $patternCount = $compiled.Keys.Count
    $totalCap = $MaxPerPattern * $patternCount

    $results = New-Object 'System.Collections.Generic.List[object]'
    $lineIndex = 0
    foreach ($line in [IO.File]::ReadLines($Path)) {
        $lineIndex += 1
        foreach ($key in $compiled.Keys) {
            if ($hits[$key] -ge $MaxPerPattern) { continue }
            $m = $compiled[$key].Match($line)
            if (-not $m.Success) { continue }
            $hits[$key] = $hits[$key] + 1
            $shown = $line.Trim()
            if ($shown.Length -gt $MaxLineLengthShown) { $shown = $shown.Substring(0, $MaxLineLengthShown) + ' ...' }
            $results.Add([pscustomobject]@{
                Pattern = $key
                Line    = $lineIndex
                Excerpt = (Hide-SecretLikeValues $shown)
            })
            if ($hits[$key] -ge $MaxPerPattern) { continue }
        }

        if (($lineIndex % 200000) -eq 0) { Write-Verbose "Find-TextLineMatches: line $lineIndex" }

        # early-exit shortcut: once every pattern is saturated stop scanning
        $total = $hits.Values.Sum()
        if ($lineIndex -gt 10 -and $total -ge $totalCap) { break }
    }
    return @($results)
}

# ---------------------------------------------------------------------------
# PowerShell script parsing (CI bootstrap use)
# ---------------------------------------------------------------------------

function Test-PsFilesParse {
    <#
        Validates one or more .ps1/.psm1/.psd1 files with the PowerShell language
        parser. Returns $true when all files parse cleanly; details land in the
        module's $script:ParseErrors (exposed via Get-PsFilesParseErrors).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Path)
    $script:ParseErrors = @()
    $ok = $true
    foreach ($file in $Path) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            $script:ParseErrors += [pscustomobject]@{
                File = $file; Line = 0; Column = 0; Message = 'File not found'
            }
            $ok = $false
            continue
        }
        $tokens = $null
        $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile(
            $file, [ref]$tokens, [ref]$errors)
        if ($errors -and $errors.Count -gt 0) {
            $ok = $false
            foreach ($err in $errors) {
                $script:ParseErrors += [pscustomobject]@{
                    File    = $file
                    Line    = $err.Extent.StartLineNumber
                    Column  = $err.Extent.StartColumnNumber
                    Message = $err.Message
                }
            }
        }
    }
    return $ok
}

function Get-PsFilesParseErrors {
    <#Returns the error objects collected by the last Test-PsFilesParse call.#>
    [CmdletBinding()]
    param()
    if (-not (Get-Variable -Name ParseErrors -Scope Script -ErrorAction SilentlyContinue)) { return @() }
    return @($script:ParseErrors)
}

# ---------------------------------------------------------------------------
# dotnet helpers
# ---------------------------------------------------------------------------

function Invoke-DotNetTool {
    <#Runs `dotnet <args>` with the arguments as separate tokens and throws on nonzero exit.#>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$DotNetArguments)
    $null = Assert-DotNetAvailable
    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($DotNetArguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# ---------------------------------------------------------------------------
# Export
# ---------------------------------------------------------------------------

Export-ModuleMember -Function @(
    'Resolve-RepoRoot',
    'Get-RepoPaths',
    'Initialize-Console',
    'Write-Step',
    'Write-StepOk',
    'Write-StepWarn',
    'Write-StepFail',
    'Write-SupportInfo',
    'Write-SupportSuccess',
    'Write-SupportError',
    'Assert-DotNetAvailable',
    'Assert-GitAvailable',
    'Find-SevenZip',
    'Invoke-CurlDownload',
    'Expand-ZipArchiveToDirectory',
    'Format-ByteSize',
    'Get-ThirdPartyToolInfo',
    'Get-ToolDirectory',
    'Confirm-InnounpTool',
    'Confirm-Il2CppDumperTool',
    'Set-Il2CppDumperBatchConfig',
    'Confirm-IfpsToolsSource',
    'Repair-IfpsLibCsprojPath',
    'Confirm-IfpsDasmWrapper',
    'Hide-SecretLikeValues',
    'Convert-BytesToAsciiSafeText',
    'Get-Latin1Encoding',
    'Invoke-FileScan',
    'Find-AsciiStringsInFile',
    'Find-TextLineMatches',
    'Test-PsFilesParse',
    'Get-PsFilesParseErrors',
    'Invoke-DotNetTool'
)
