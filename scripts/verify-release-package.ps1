<#
.SYNOPSIS
    Verifies an ADOFAI.Renderist release zip against Phase 2.0 packaging rules.

.DESCRIPTION
    Phase 2.0 screenshot sequence MVP — packaging rules unchanged since 1.4.

    Asserts the following on the supplied zip:
      * Top-level files only — exactly:
            Info.json
            ADOFAI.Renderist.dll
            LICENSE
        No subdirectories. No other files. No top-level folder.
      * No banned content (README*, AGENTS.md, CLAUDE.md, *.pdb, *.xml,
        *.cache, *.config, Settings.xml, Log.txt, bin/, obj/, .git/, .vscode/,
        build/, references/, UnityModManager.dll, 0Harmony.dll,
        Assembly-CSharp*.dll, UnityEngine*.dll, UnityPlayer.dll,
        ADOFAI.Renderist.dll.<digits>.cache).
      * Info.json fields:
            Id            == ADOFAI.Renderist
            AssemblyName  == ADOFAI.Renderist.dll
            EntryMethod   == ADOFAI.Renderist.ModEntry.Load
            ManagerVersion == 0.32.5
            Version       == <expected>   (default: mod/Info.json Version)
      * DLL FileVersion or ProductVersion equals Info.json Version,
        guarding against packaging a stale build.
      * If a sibling .sha256 file exists, the zip's SHA256 must match it.
        Missing .sha256 is NOT a failure.

.PARAMETER ZipPath
    Path to the release zip to verify.

.PARAMETER ExpectedVersion
    Optional. If omitted, the script reads mod/Info.json from the repository
    to determine the expected version.

.PARAMETER Strict
    Reserved for future stricter assertions. Currently has no extra effect.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$ZipPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Checks   = 0

function Add-Failure([string]$msg) {
    $script:Failures.Add($msg) | Out-Null
}
function Assert-True([bool]$cond, [string]$msg) {
    $script:Checks++
    if (-not $cond) { Add-Failure $msg }
}

# ---------- repo root for fallback Info.json lookup --------------------------

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

# ---------- 1. validate zip path ---------------------------------------------

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    Write-Error "Zip not found: $ZipPath"
    exit 1
}
$zipFull = [System.IO.Path]::GetFullPath($ZipPath)
if ([System.IO.Path]::GetExtension($zipFull).ToLowerInvariant() -ne '.zip') {
    Write-Error "Not a .zip file: $zipFull"
    exit 1
}

# ---------- 2. resolve expected version --------------------------------------

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $repoInfoJson = Join-Path $repoRoot 'mod\Info.json'
    if (-not (Test-Path -LiteralPath $repoInfoJson -PathType Leaf)) {
        Write-Error "ExpectedVersion not supplied and mod/Info.json not found at $repoInfoJson"
        exit 1
    }
    try {
        $info = Get-Content -LiteralPath $repoInfoJson -Raw | ConvertFrom-Json
    } catch {
        Write-Error "Failed to parse $repoInfoJson : $($_.Exception.Message)"
        exit 1
    }
    $ExpectedVersion = [string]$info.Version
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        Write-Error "mod/Info.json has no Version."
        exit 1
    }
}

Write-Host "==> Verifying $zipFull (expected version: $ExpectedVersion)" -ForegroundColor Cyan

# ---------- 3. optional sidecar SHA256 ---------------------------------------

$sidecar = "$zipFull.sha256"
if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
    $expectedHash = ((Get-Content -LiteralPath $sidecar -Raw) -split '\s+')[0].Trim().ToUpperInvariant()
    if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
        $actualHash = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash.ToUpperInvariant()
        Assert-True ($expectedHash -eq $actualHash) "Zip SHA256 mismatch: sidecar=$expectedHash actual=$actualHash"
    }
} else {
    Write-Host "  (no .sha256 sidecar; skipping zip hash check)"
}

# ---------- 4. enumerate zip entries without extracting ----------------------

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
try {
    $entries = @($archive.Entries)

    $allowedTopLevel = @('Info.json','ADOFAI.Renderist.dll','LICENSE')
    $bannedNamePatterns = @(
        '^README(\..*)?$',
        '^AGENTS\.md$',
        '^CLAUDE\.md$',
        '^Settings\.xml$',
        '^Log\.txt$',
        '^UnityModManager\.dll$',
        '^0Harmony\.dll$',
        '^Assembly-CSharp.*\.dll$',
        '^UnityEngine.*\.dll$',
        '^UnityPlayer\.dll$',
        '^ADOFAI\.Renderist\.dll\.[0-9]+\.cache$'
    )
    $bannedExtensions = @('.pdb','.xml','.cache','.config')
    $bannedFolderRoots = @('bin','obj','.git','.vscode','.vs','.idea','build','references')

    $observedTopLevel = New-Object System.Collections.Generic.List[string]

    foreach ($e in $entries) {
        # ZipArchive uses '/' as the path separator regardless of OS.
        $full = $e.FullName
        # Skip explicit directory entries that are exactly a slash-terminated empty entry.
        if ($e.Length -eq 0 -and $full.EndsWith('/')) {
            # Even directory entries are not allowed at any level in our package.
            Add-Failure "Zip contains a directory entry '$full' but only top-level files are allowed."
            continue
        }

        if ($full.Contains('/')) {
            Add-Failure "Zip contains a non-top-level entry '$full'. Only top-level files are allowed."
            $segments = $full.Split('/')
            $root = $segments[0]
            if ($bannedFolderRoots -contains $root) {
                Add-Failure "Zip contains entry under banned folder '$root/': $full"
            }
            continue
        }

        $observedTopLevel.Add($full)

        if ($allowedTopLevel -notcontains $full) {
            Add-Failure "Zip contains unexpected top-level file '$full'."
        }

        foreach ($pat in $bannedNamePatterns) {
            if ($full -match $pat) {
                Add-Failure "Zip contains banned file matching /$pat/: $full"
            }
        }
        $ext = [System.IO.Path]::GetExtension($full).ToLowerInvariant()
        if ($bannedExtensions -contains $ext) {
            Add-Failure "Zip contains banned extension '$ext': $full"
        }
    }

    foreach ($required in $allowedTopLevel) {
        Assert-True ($observedTopLevel -contains $required) "Required top-level file missing from zip: $required"
    }
    Assert-True ($observedTopLevel.Count -eq $allowedTopLevel.Count) ("Top-level file count mismatch: expected {0}, got {1} ({2})" -f $allowedTopLevel.Count, $observedTopLevel.Count, ($observedTopLevel -join ', '))

    # ---------- 5. extract Info.json + DLL to a temp dir for deeper checks ---

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("adofai-renderist-verify-{0}" -f ([Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        foreach ($e in $entries) {
            if ($allowedTopLevel -contains $e.FullName) {
                $dst = Join-Path $tempDir $e.FullName
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
            }
        }

        $infoPath = Join-Path $tempDir 'Info.json'
        $dllPath  = Join-Path $tempDir 'ADOFAI.Renderist.dll'

        if (-not (Test-Path -LiteralPath $infoPath -PathType Leaf)) {
            Add-Failure "Info.json was not present after extraction (zip may be malformed)."
        } else {
            try {
                $infoObj = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
            } catch {
                Add-Failure "Failed to parse Info.json inside zip: $($_.Exception.Message)"
                $infoObj = $null
            }
            if ($infoObj -ne $null) {
                Assert-True ([string]$infoObj.Id            -eq 'ADOFAI.Renderist')                     "Info.json Id mismatch: '$($infoObj.Id)'"
                Assert-True ([string]$infoObj.AssemblyName  -eq 'ADOFAI.Renderist.dll')                 "Info.json AssemblyName mismatch: '$($infoObj.AssemblyName)'"
                Assert-True ([string]$infoObj.EntryMethod   -eq 'ADOFAI.Renderist.ModEntry.Load')       "Info.json EntryMethod mismatch: '$($infoObj.EntryMethod)'"
                Assert-True ([string]$infoObj.ManagerVersion -eq '0.32.5')                              "Info.json ManagerVersion mismatch: '$($infoObj.ManagerVersion)'"
                Assert-True ([string]$infoObj.Version       -eq $ExpectedVersion)                       "Info.json Version mismatch: expected '$ExpectedVersion', got '$($infoObj.Version)'"
            }
        }

        if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            Add-Failure "ADOFAI.Renderist.dll was not present after extraction."
        } else {
            $vi = (Get-Item -LiteralPath $dllPath).VersionInfo
            $fileVer    = ($vi.FileVersion    | ForEach-Object { $_ }) -as [string]
            $productVer = ($vi.ProductVersion | ForEach-Object { $_ }) -as [string]

            function Versions-Match([string]$a, [string]$b) {
                if ([string]::IsNullOrWhiteSpace($a) -or [string]::IsNullOrWhiteSpace($b)) { return $false }
                # SDK ProductVersion can carry SemVer metadata like '0.1.4+<sha>'
                # or pre-release like '0.1.4-rc.1'. Strip metadata / pre-release
                # suffixes before comparing numeric components.
                $ca = ($a.Trim() -split '[-+]', 2)[0]
                $cb = ($b.Trim() -split '[-+]', 2)[0]
                $pa = $ca -split '\.'
                $pb = $cb -split '\.'
                $max = [Math]::Max($pa.Length, $pb.Length)
                for ($i = 0; $i -lt $max; $i++) {
                    $ai = if ($i -lt $pa.Length) { [int]$pa[$i] } else { 0 }
                    $bi = if ($i -lt $pb.Length) { [int]$pb[$i] } else { 0 }
                    if ($ai -ne $bi) { return $false }
                }
                return $true
            }

            $dllMatchesFile    = Versions-Match $fileVer    $ExpectedVersion
            $dllMatchesProduct = Versions-Match $productVer $ExpectedVersion
            Assert-True ($dllMatchesFile -or $dllMatchesProduct) ("DLL version does not match Info.json Version '{0}'. FileVersion='{1}' ProductVersion='{2}'." -f $ExpectedVersion, $fileVer, $productVer)
        }
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} finally {
    $archive.Dispose()
}

# ---------- 6. report --------------------------------------------------------

if ($script:Failures.Count -eq 0) {
    Write-Host ("PASS — {0} checks, 0 failures." -f $script:Checks) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("FAIL — {0} checks, {1} failures:" -f $script:Checks, $script:Failures.Count) -ForegroundColor Red
    foreach ($f in $script:Failures) {
        Write-Host "  - $f" -ForegroundColor Red
    }
    exit 1
}
