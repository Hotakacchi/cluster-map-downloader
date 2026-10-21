#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies that the repository contains only redistributable code & docs:
    no client binaries, no world assets, no reverse-engineering artifacts,
    no secret-shaped literals, and no tracked files inside ignored dirs.

.DESCRIPTION
    Checks (order matters for the printed sections):
      1. If a git repository: everything listed by `git ls-files` must pass
         the path/extension/name rules. Without git, the check degrades to
         scanning the working tree while excluding note-worthy ignored dirs
         (a warning is printed).
      2. Anti-pattern scan on text files: Google / Stripe / AWS / OAuth /
         GitHub-shaped secrets, PEM private keys, and recorded token URLs.
      3. .gitignore coverage of the known local-only directories.
      4. No binary/executable blob may be tracked (by extension).

    Exit codes: 0 = policy satisfied, 1 = violations found.
#>
[CmdletBinding()]
param(
    # Skip git checks (e.g. when run inside a sandbox without git metadata)
    [switch]$NoGit,
    # Maximum bytes of a candidate text file to scan
    [ValidateRange(1024, 4194304)][int]$MaxTextScanBytes = 1048576
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath.TrimEnd('\')
$inGitRepo = $false
try {
    $null = & git -C $repoRoot rev-parse --is-inside-work-tree 2>$null
    $inGitRepo = ($LASTEXITCODE -eq 0)
} catch { $inGitRepo = $false }

$violations = [System.Collections.Generic.List[string]]::new()

function Add-Violation {
    param([Parameter(Mandatory)][string]$Message)
    $violations.Add($Message)
}

Write-Host 'check-repo-policy — bundle content policy' -ForegroundColor Cyan
Write-Host ("repository: {0} (git: {1})" -f $repoRoot, $inGitRepo) -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# The ignore list mirrors .gitignore; the policy is stricter than the ignore
# file: these dirs/extensions must not be tracked AT ALL.
# ---------------------------------------------------------------------------
$forbiddenDirPrefixes = @(
    '.work/', '.temp/', '.tmp/', 'downloads/', 'output/', 'cache/',
    'artifacts/', 'artifacts-local/', 'third-party/', 'installer/',
    'credentials/', 'DummyDll/', 'TestResults/', 'bin/', 'obj/',
    'cluster_Data/', 'D3D12/', 'MonoBleedingEdge/', 'Addressables/'
)
$forbiddenExtensions = @(
    '.exe', '.dll', '.7z', '.rar', '.zip', '.unity3d', '.assetbundle',
    '.bundle', '.ab', '.unitypackage', '.app', '.icns', '.dat', '.bin',
    '.cob', '.pfx', '.pem', '.trx', '.binlog', '.so', '.dylib'
)
$forbiddenFileNamePatterns = @(
    '(?i)^cluster(_win.*)?\.(exe|7z)$',
    '(?i)^gameassembly\.dll$',
    '(?i)^unityplayer\.dll$',
    '(?i)^global-metadata\.dat$',
    '(?i)^compiledcode\.(bin|dis\.txt)$',
    '(?i)^install_script\.iss$',
    '(?i)^dump\.cs$',
    '(?i)^(script\.json|il2cpp\.h)$'
)
$secretPatterns = @(
    @{ Name = 'Google API key literal (AIza...)';        Regex = 'AIza[0-9A-Za-z_-]{10,}' }
    @{ Name = 'Stripe publishable key (pk_live/...)';    Regex = 'pk_(?:live|test)_[0-9A-Za-z]{10,}' }
    @{ Name = 'Stripe secret key (sk_live/...)';         Regex = 'sk_(?:live|test)_[0-9A-Za-z]{10,}' }
    @{ Name = 'AWS access key id (AKIA...)';             Regex = 'AKIA[0-9A-Z]{12,}' }
    @{ Name = 'OAuth access token (ya29...)';            Regex = 'ya29\.[0-9A-Za-z_-]{10,}' }
    @{ Name = 'GitHub token (ghp_/github_pat_)';         Regex = 'ghp_[0-9A-Za-z]{20,}|github_pat_[0-9A-Za-z_]{20,}' }
    @{ Name = 'Slack token (xox)';                       Regex = 'xox[bpas]-[0-9A-Za-z-]{10,}' }
    @{ Name = 'PEM private key block';                   Regex = '-----BEGIN [A-Z ]*PRIVATE KEY-----' }
    @{ Name = 'Signed URL markers';                      Regex = 'X-Amz-(Signature|Credential)=(?:"|[A-Za-z0-9+/=]{8,})' }
    @{ Name = 'FIREBASE_WEB_API_KEY-style placeholders with value'; Regex = '(?i)firebase[_-]?web[_-]?api[_-]?key\s*[:=]\s*["''][^"'']{8,}' }
)

$requiredGitignoreLines = @(
    '.work/',
    '.temp/',
    'downloads/',
    'output/',
    'artifacts-local/',
    'cache/',
    '[Bb]in/',
    '[Oo]bj/',
    'cluster_win.exe',
    'GameAssembly.dll',
    'global-metadata.dat'
)

# ---------------------------------------------------------------------------
# 1. File-name/policy rules on the candidate file list
# ---------------------------------------------------------------------------
if ($inGitRepo -and -not $NoGit) {
    $trackedFiles = @(& git -C $repoRoot ls-files | Where-Object { $_.Trim().Length -gt 0 })
    Write-Host ("Checking {0} tracked files" -f $trackedFiles.Count)
    foreach ($rel in $trackedFiles) {
        $normalized = ($rel -replace '\\', '/')
        foreach ($prefix in $forbiddenDirPrefixes) {
            if ($normalized -like ($prefix + '*')) {
                Add-Violation ("tracked forbidden directory entry: {0}" -f $rel)
                break
            }
        }
        $extension = [IO.Path]::GetExtension($rel)
        if ($forbiddenExtensions -contains $extension.ToLowerInvariant()) {
            Add-Violation ("tracked forbidden binary/asset extension: {0}" -f $rel)
        }
        $fileName = [IO.Path]::GetFileName($rel)
        foreach ($pattern in $forbiddenFileNamePatterns) {
            if ($fileName -match $pattern) {
                Add-Violation ("tracked forbidden reverse-engineering artifact name: {0}" -f $rel)
                break
            }
        }
    }
}
else {
    Write-Host ('No git metadata: falling back to a best-effort tree scan (excluding ignored dirs).') -ForegroundColor Yellow
    $ignoredDirs = @('.git', '.work', '.temp', '.tmp', 'downloads', 'output', 'cache',
        'artifacts', 'artifacts-local', 'third-party', 'installer', 'credentials',
        'TestResults', 'bin', 'obj', '.vs', '.vscode', '.idea', 'node_modules')
    $treeFiles = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            # exclude anything living under ANY segment of a locally-ignored directory
            # (bin/, obj/, .work/... may sit nested under src/ or tests/)
            $relative = $_.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
            $segments = $relative -split '/'
            if (-not $relative) { return $false }
            foreach ($segment in $segments) {
                if ($ignoredDirs -contains $segment) { return $false }
            }
            return $true
        })
    Write-Host ("Scanning {0} candidate files" -f $treeFiles.Count)
    foreach ($file in $treeFiles) {
        $relative = $file.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
        $extension = [IO.Path]::GetExtension($relative)
        if ($forbiddenExtensions -contains $extension.ToLowerInvariant()) {
            Add-Violation ("forbidden binary/asset extension present in tree (not ignored?): {0}" -f $relative)
        }
        $fileName = [IO.Path]::GetFileName($relative)
        foreach ($pattern in $forbiddenFileNamePatterns) {
            if ($fileName -match $pattern) {
                Add-Violation ("forbidden reverse-engineering artifact name in tree (not ignored?): {0}" -f $relative)
                break
            }
        }
    }
    Write-Host ('Note: without git metadata this check cannot prove tracked-status. ' +
        'Run again inside the repository (git checkout) for the authoritative result.') -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 2. Secret-shaped literal scan on text candidates
# ---------------------------------------------------------------------------
function Test-TextCandidate {
    param([string]$extension)
    $extension = $extension.ToLowerInvariant()
    return ($extension -in @('.cs', '.ps1', '.psm1', '.psd1', '.md', '.json', '.yml',
                             '.yaml', '.txt', '.iss', '.props', '.editorconfig', '.gitignore',
                             '.sln', '.csproj', '.readme', '.license', '.xml', '.config',
                             '.npmrc', '.sh', '.bat', '.cmd', '.in', '.sample', ''))
}

$candidateFiles = @()
if ($inGitRepo -and -not $NoGit) {
    $candidateFiles = @($trackedFiles)
}
else {
    $candidateFiles = @($treeFiles | ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1) })
}

$scannedCount = 0
foreach ($rel in $candidateFiles) {
    $extension = [IO.Path]::GetExtension($rel)
    if (-not (Test-TextCandidate -extension $extension)) { continue }
    $fullPath = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    if ((Get-Item -LiteralPath $fullPath).Length -gt $MaxTextScanBytes) { continue }
    $scannedCount = $scannedCount + 1
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($fullPath)) {
        $lineNumber = $lineNumber + 1
        foreach ($secret in $secretPatterns) {
            if ($line -match $secret.Regex) {
                Add-Violation ("{0}:{1}: {2}" -f $rel, $lineNumber, $secret.Name)
            }
        }
    }
}
Write-Host ("Secret-pattern scan executed on {0} text file(s)." -f $scannedCount)

# ---------------------------------------------------------------------------
# 3. .gitignore must still cover the local-only directories
# ---------------------------------------------------------------------------
$gitignorePath = Join-Path $repoRoot '.gitignore'
if (Test-Path -LiteralPath $gitignorePath -PathType Leaf) {
    $gitignoreText = (Get-Content -LiteralPath $gitignorePath -Raw -ErrorAction SilentlyContinue)
    foreach ($required in $requiredGitignoreLines) {
        if ($gitignoreText -notmatch [regex]::Escape($required)) {
            Add-Violation (".gitignore no longer covers required local-only pattern: {0}" -f $required)
        }
    }
}
else {
    Add-Violation '.gitignore missing (required for the no-binary policy)'
}

# ---------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------
if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host ("policy violations: {0}" -f $violations.Count) -ForegroundColor Red
    foreach ($violation in $violations) { Write-Host ("  - {0}" -f $violation) -ForegroundColor Red }
    Write-Host ""
    Write-Host 'Fix the violations before committing. See .gitignore and NOTICE.md.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'policy OK: no forbidden artifacts, no secret-shaped literals, gitignore coverage verified.' -ForegroundColor Green
exit 0
