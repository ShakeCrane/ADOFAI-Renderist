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

    Validates the local Mono/Managed baseline and required Phase 1 DLLs.
    Writes build/local.props from build/local.props.example with the
    AdofaiInstallDir value substituted.

    This script never copies any game DLLs and never modifies anything
    outside this repository except build/local.props.

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

function Get-FileVersion([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
}

function Test-FileVersion([string]$name, [string]$path, [string]$expectedVersion) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Fail "$name (expected at: $path)"
        return $false
    }

    $actualVersion = Get-FileVersion $path
    if ($actualVersion -eq $expectedVersion) {
        Write-Ok "$name $actualVersion"
        return $true
    }

    Write-Fail "$name version mismatch: expected $expectedVersion, found $actualVersion"
    return $false
}

function Test-RequiredFile([string]$name, [string]$path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Write-Ok $name
        return $true
    }

    Write-Fail "$name (expected at: $path)"
    return $false
}

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
$monoDir    = Join-Path $resolved 'MonoBleedingEdge'
$gameAssembly = Join-Path $resolved 'GameAssembly.dll'

# ----------- 2. Validate required DLLs -------------------------------------

Write-Section 'Validating local ADOFAI baseline'

$missing = 0

if (Test-Path -LiteralPath $resolved -PathType Container) {
    Write-Ok "ADOFAI install directory: $resolved"
} else {
    Write-Fail "ADOFAI install directory missing: $resolved"
    $missing++
}

if (Test-Path -LiteralPath $managedDir -PathType Container) {
    Write-Ok "Managed directory: $managedDir"
} else {
    Write-Fail "Managed directory missing: $managedDir"
    $missing++
}

if (Test-Path -LiteralPath $monoDir -PathType Container) {
    Write-Ok 'MonoBleedingEdge/ present (Mono/Managed baseline)'
} else {
    Write-Fail "MonoBleedingEdge/ missing (expected at: $monoDir)"
    $missing++
}

if (Test-Path -LiteralPath $gameAssembly -PathType Leaf) {
    Write-Miss "GameAssembly.dll present — IL2CPP risk; current baseline expects Mono/Managed"
} else {
    Write-Ok 'GameAssembly.dll not present (Mono/Managed baseline)'
}

Write-Section 'Validating Phase 1 required DLLs'

if (-not (Test-FileVersion 'UnityModManager.dll' (Join-Path $ummDir 'UnityModManager.dll') '0.32.5.0')) { $missing++ }
if (-not (Test-FileVersion '0Harmony.dll' (Join-Path $ummDir '0Harmony.dll') '2.3.6.0')) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.CoreModule.dll' (Join-Path $managedDir 'UnityEngine.CoreModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.IMGUIModule.dll' (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.ScreenCaptureModule.dll' (Join-Path $managedDir 'UnityEngine.ScreenCaptureModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'Assembly-CSharp.dll' (Join-Path $managedDir 'Assembly-CSharp.dll'))) { $missing++ }

if ($missing -gt 0) {
    Write-Fail "$missing required baseline item(s) missing or mismatched. Aborting without writing build/local.props."
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
Write-Section 'Phase 4 pre-check (informational only — not referenced for Phase 1 build)'
$firstpass = Join-Path $managedDir 'Assembly-CSharp-firstpass.dll'
if (Test-Path -LiteralPath $firstpass -PathType Leaf) { Write-Ok 'Assembly-CSharp-firstpass.dll' } else { Write-Miss 'Assembly-CSharp-firstpass.dll (not present)' }

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
