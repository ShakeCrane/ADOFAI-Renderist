<#
.SYNOPSIS
    Generates build/local.props pointing at the local ADOFAI install and
    verifies that the Phase 1 reference DLLs exist.

.DESCRIPTION
    Strategy:
      1. If -AdofaiDir was supplied, use it.
      2. Else if build/local.props already exists and parses cleanly, reuse it.
      3. Else try common Steam install paths.
      4. Else prompt the developer.

    Validates four mandatory DLLs (hard fail) and one optional DLL (warn only).
    Writes build/local.props from build/local.props.example with the
    AdofaiInstallDir value substituted.

    This script never copies any game DLLs and never modifies anything
    outside this repository.

.PARAMETER AdofaiDir
    Path to the ADOFAI install root (the folder containing
    "A Dance of Fire and Ice.exe").

.PARAMETER NonInteractive
    Skip the interactive prompt. Fails if a path cannot be auto-detected.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-references.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-references.ps1 -AdofaiDir "D:\Games\ADOFAI"
#>

[CmdletBinding()]
param(
    [string]$AdofaiDir,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$buildDir   = Join-Path $repoRoot 'build'
$exampleProps = Join-Path $buildDir 'local.props.example'
$localProps   = Join-Path $buildDir 'local.props'

function Write-Section($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)      { Write-Host "  + $msg" -ForegroundColor Green }
function Write-Miss($msg)    { Write-Host "  ! $msg" -ForegroundColor Yellow }
function Write-Fail($msg)    { Write-Host "  x $msg" -ForegroundColor Red }

function Test-AdofaiRoot([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $false }
    $exe = Join-Path $path 'A Dance of Fire and Ice.exe'
    return Test-Path -LiteralPath $exe -PathType Leaf
}

function Read-ExistingInstallDir([string]$propsPath) {
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { return $null }
    try {
        [xml]$xml = Get-Content -LiteralPath $propsPath -Raw
        $node = $xml.Project.PropertyGroup.AdofaiInstallDir
        if ($node) { return [string]$node }
    } catch {
        Write-Miss "Failed to parse existing $propsPath : $($_.Exception.Message)"
    }
    return $null
}

function Find-AdofaiCandidates {
    $candidates = New-Object System.Collections.Generic.List[string]
    $defaultRelative = 'steamapps\common\A Dance of Fire and Ice'

    $steamRoots = @(
        "$env:ProgramFiles\Steam",
        "${env:ProgramFiles(x86)}\Steam"
    )
    foreach ($drive in 'C','D','E','F','G') {
        $steamRoots += "$drive`:\Steam"
        $steamRoots += "$drive`:\SteamLibrary"
        $steamRoots += "$drive`:\Program Files\Steam"
        $steamRoots += "$drive`:\Program Files (x86)\Steam"
    }
    foreach ($root in $steamRoots | Where-Object { $_ }) {
        $candidates.Add((Join-Path $root $defaultRelative))
    }

    # libraryfolders.vdf parsing — best-effort, may not exist.
    foreach ($root in $steamRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            try {
                $content = Get-Content -LiteralPath $vdf -Raw
                $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
                foreach ($m in $matches) {
                    $lib = $m.Groups[1].Value -replace '\\\\','\'
                    $candidates.Add((Join-Path $lib $defaultRelative))
                }
            } catch {
                Write-Miss "Failed to parse $vdf : $($_.Exception.Message)"
            }
        }
    }

    return ($candidates | Select-Object -Unique)
}

# ----------- 1. Determine AdofaiInstallDir ---------------------------------

Write-Section 'Resolving ADOFAI install directory'

$resolved = $null

if ($AdofaiDir) {
    if (Test-AdofaiRoot $AdofaiDir) {
        $resolved = (Resolve-Path -LiteralPath $AdofaiDir).Path
        Write-Ok "Using -AdofaiDir: $resolved"
    } else {
        Write-Fail "-AdofaiDir was supplied but does not look like an ADOFAI install root: $AdofaiDir"
        exit 1
    }
}

if (-not $resolved) {
    $existing = Read-ExistingInstallDir $localProps
    if ($existing -and (Test-AdofaiRoot $existing)) {
        $resolved = $existing
        Write-Ok "Reusing existing build/local.props: $resolved"
    } elseif ($existing) {
        Write-Miss "Existing build/local.props points at an invalid path: $existing"
    }
}

if (-not $resolved) {
    foreach ($cand in Find-AdofaiCandidates) {
        if (Test-AdofaiRoot $cand) {
            $resolved = (Resolve-Path -LiteralPath $cand).Path
            Write-Ok "Auto-detected: $resolved"
            break
        }
    }
}

if (-not $resolved) {
    if ($NonInteractive) {
        Write-Fail 'Could not auto-detect ADOFAI install. Re-run with -AdofaiDir <path>.'
        exit 1
    }
    Write-Miss 'Could not auto-detect ADOFAI install.'
    $answer = Read-Host 'Enter the full path to your ADOFAI install root (the folder containing "A Dance of Fire and Ice.exe")'
    if (Test-AdofaiRoot $answer) {
        $resolved = (Resolve-Path -LiteralPath $answer).Path
        Write-Ok "Using: $resolved"
    } else {
        Write-Fail "Path does not look like an ADOFAI install root: $answer"
        exit 1
    }
}

$managedDir = Join-Path $resolved 'A Dance of Fire and Ice_Data\Managed'
$ummDir     = Join-Path $managedDir 'UnityModManager'

# ----------- 2. Validate required DLLs -------------------------------------

Write-Section 'Validating Phase 1 required DLLs'

$required = @(
    @{ Name = 'UnityModManager.dll';        Path = (Join-Path $ummDir     'UnityModManager.dll') },
    @{ Name = '0Harmony.dll';               Path = (Join-Path $ummDir     '0Harmony.dll') },
    @{ Name = 'UnityEngine.CoreModule.dll'; Path = (Join-Path $managedDir 'UnityEngine.CoreModule.dll') },
    @{ Name = 'UnityEngine.IMGUIModule.dll';Path = (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll') }
)

$missing = 0
foreach ($r in $required) {
    if (Test-Path -LiteralPath $r.Path -PathType Leaf) {
        Write-Ok "$($r.Name)"
    } else {
        Write-Fail "$($r.Name)  (expected at: $($r.Path))"
        $missing++
    }
}

if ($missing -gt 0) {
    Write-Fail "$missing required DLL(s) missing. Aborting without writing build/local.props."
    exit 1
}

# Soft-check UnityEngine.dll
$unityUmbrella = Join-Path $managedDir 'UnityEngine.dll'
if (Test-Path -LiteralPath $unityUmbrella -PathType Leaf) {
    Write-Ok 'UnityEngine.dll (optional, present)'
} else {
    Write-Miss 'UnityEngine.dll (optional, not present — Phase 1 will continue without it)'
}

# Phase 4 pre-check — informational only.
Write-Section 'Phase 4 pre-check (informational only — not required for Phase 1 build)'
foreach ($n in 'Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll') {
    $p = Join-Path $managedDir $n
    if (Test-Path -LiteralPath $p -PathType Leaf) { Write-Ok $n } else { Write-Miss "$n (not present)" }
}

# ----------- 3. Generate build/local.props ---------------------------------

Write-Section 'Writing build/local.props'

if (-not (Test-Path -LiteralPath $exampleProps -PathType Leaf)) {
    Write-Fail "Template not found: $exampleProps"
    exit 1
}

# Use the example as the template and substitute AdofaiInstallDir.
$template = Get-Content -LiteralPath $exampleProps -Raw
$rendered = [regex]::Replace(
    $template,
    '(?s)<AdofaiInstallDir>.*?</AdofaiInstallDir>',
    "<AdofaiInstallDir>$resolved</AdofaiInstallDir>"
)

# Strip example-only comments to keep the generated file small.
# (Optional cosmetic — keep it simple: write as-is.)

if (-not (Test-Path -LiteralPath $buildDir -PathType Container)) {
    New-Item -ItemType Directory -Path $buildDir | Out-Null
}

Set-Content -LiteralPath $localProps -Value $rendered -Encoding UTF8
Write-Ok "Wrote $localProps"

Write-Section 'Done.'
Write-Host "Next step: dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release" -ForegroundColor Cyan
