<#
.SYNOPSIS
    Deploys the built ADOFAI.Renderist mod to <ADOFAI>\Mods\ADOFAI.Renderist\.

.DESCRIPTION
    Strict, narrow-scope deployment script. By design:

      * The destination directory is HARD-CODED to "ADOFAI.Renderist" under
        the ADOFAI install's Mods folder. No -ModName / -OutDir parameters.
      * The script refuses to write outside <ADOFAI>\Mods\ADOFAI.Renderist\.
        Any path mismatch is a hard failure — no fallback, no recovery.
      * The script refuses to delete or modify any other Mod folder.
      * The script refuses to touch ADOFAI's own game files
        (UnityPlayer.dll, winhttp.dll, A Dance of Fire and Ice.exe,
         A Dance of Fire and Ice_Data\, doorstop_config.ini, etc.).
      * If the destination directory already exists, every file inside it
        must belong to this mod (whitelist below) before any write happens.
      * Only two files are copied:
            1) ADOFAI.Renderist.dll (from bin\Release)
            2) Info.json            (from mod\)
        File hashes are verified post-copy.

    The ADOFAI install path is read from build/local.props ($(AdofaiInstallDir)).
    Run scripts/prepare-references.ps1 first if that file does not exist.

.PARAMETER Configuration
    Build configuration to deploy. Default: Release.

.PARAMETER WhatIf
    Print actions without performing them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/copy-to-mods.ps1
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# ---------- repo-relative paths ------------------------------------------------

$repoRoot   = Split-Path -Parent $PSScriptRoot
$localProps = Join-Path $repoRoot 'build\local.props'
$builtDll   = Join-Path $repoRoot ("src\ADOFAI.Renderist\bin\{0}\ADOFAI.Renderist.dll" -f $Configuration)
$infoJson   = Join-Path $repoRoot 'mod\Info.json'

function Fail($msg) {
    Write-Error $msg
    exit 1
}

# ---------- 1. Read AdofaiInstallDir from build/local.props -------------------

if (-not (Test-Path -LiteralPath $localProps -PathType Leaf)) {
    Fail "build/local.props not found. Run scripts/prepare-references.ps1 first."
}

[xml]$xml = Get-Content -LiteralPath $localProps -Raw
$installDir = [string]$xml.Project.PropertyGroup.AdofaiInstallDir
if ([string]::IsNullOrWhiteSpace($installDir)) {
    Fail "AdofaiInstallDir is not defined in build/local.props."
}

# Normalize using GetFullPath (does not require the path to exist).
try {
    $installDirAbs = [System.IO.Path]::GetFullPath($installDir)
} catch {
    Fail "Invalid AdofaiInstallDir: $installDir"
}
if (-not (Test-Path -LiteralPath $installDirAbs -PathType Container)) {
    Fail "AdofaiInstallDir does not exist: $installDirAbs"
}
$gameExe = Join-Path $installDirAbs 'A Dance of Fire and Ice.exe'
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    Fail "AdofaiInstallDir does not look like an ADOFAI install (missing game exe): $installDirAbs"
}

# ---------- 2. Compute and validate the destination path ---------------------

$modsRoot   = [System.IO.Path]::GetFullPath((Join-Path $installDirAbs 'Mods'))
$destDir    = [System.IO.Path]::GetFullPath((Join-Path $modsRoot 'ADOFAI.Renderist'))

# Hard rule: $destDir MUST live strictly under $modsRoot, and the segment after
# $modsRoot MUST be exactly 'ADOFAI.Renderist' — nothing else, no traversal.
$sep = [System.IO.Path]::DirectorySeparatorChar
$modsRootWithSep = if ($modsRoot.EndsWith($sep)) { $modsRoot } else { $modsRoot + $sep }
if (-not $destDir.StartsWith($modsRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "Refusing to write outside <ADOFAI>\Mods\: $destDir"
}
$tail = $destDir.Substring($modsRootWithSep.Length).TrimEnd($sep)
if ($tail -ne 'ADOFAI.Renderist') {
    Fail "Destination tail must be exactly 'ADOFAI.Renderist', got: '$tail'"
}

# Hard rule: refuse if the resolved destination overlaps with ADOFAI game data.
$forbiddenPrefixes = @(
    [System.IO.Path]::GetFullPath((Join-Path $installDirAbs 'A Dance of Fire and Ice_Data'))
)
foreach ($p in $forbiddenPrefixes) {
    $pWithSep = if ($p.EndsWith($sep)) { $p } else { $p + $sep }
    if ($destDir.StartsWith($pWithSep, [System.StringComparison]::OrdinalIgnoreCase) -or
        $destDir.Equals($p, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "Destination resolves into game data area, refusing: $destDir"
    }
}

# ---------- 3. Validate source artifacts -------------------------------------

if (-not (Test-Path -LiteralPath $builtDll -PathType Leaf)) {
    Fail "Built DLL not found: $builtDll  (run a Release build first)"
}
if (-not (Test-Path -LiteralPath $infoJson -PathType Leaf)) {
    Fail "mod/Info.json not found: $infoJson"
}

# ---------- 4. Destination-folder ownership check ----------------------------

$allowedFiles = @(
    'ADOFAI.Renderist.dll',
    'Info.json',
    'Settings.xml',                  # UMM settings persistence
    'ADOFAI.Renderist.pdb'           # optional symbols
)

if (Test-Path -LiteralPath $destDir -PathType Container) {
    Get-ChildItem -LiteralPath $destDir -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            Fail "Destination $destDir contains an unexpected subdirectory '$($_.Name)'. Refusing to write — please remove it manually if it is safe to do so."
        }
        if ($allowedFiles -notcontains $_.Name) {
            Fail "Destination $destDir contains an unexpected file '$($_.Name)' that does not belong to this mod. Refusing to write."
        }
    }
} else {
    if ($WhatIf) {
        Write-Host "WhatIf: would create $destDir"
    } else {
        New-Item -ItemType Directory -Path $destDir | Out-Null
    }
}

# ---------- 5. Copy + hash verification --------------------------------------

function Copy-File([string]$src, [string]$dst) {
    if ($WhatIf) {
        Write-Host "WhatIf: would copy $src -> $dst"
        return
    }
    Copy-Item -LiteralPath $src -Destination $dst -Force

    $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
    if ($srcHash -ne $dstHash) {
        Fail "Hash mismatch after copying $src -> $dst"
    }
    Write-Host "  + $([System.IO.Path]::GetFileName($dst))  (sha256: $($srcHash.Substring(0,12))…)"
}

Write-Host "==> Deploying to $destDir" -ForegroundColor Cyan
Copy-File $builtDll (Join-Path $destDir 'ADOFAI.Renderist.dll')
Copy-File $infoJson (Join-Path $destDir 'Info.json')

Write-Host 'Done.' -ForegroundColor Cyan
