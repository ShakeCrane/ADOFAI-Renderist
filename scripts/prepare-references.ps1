<#
.SYNOPSIS
    Generates build/local.props pointing at the local ADOFAI install and
    verifies that the Phase 1.3 reference DLL baseline is available.

.DESCRIPTION
    Strategy:
      1. If -AdofaiDir was supplied, use it.
      2. Else if build/local.props already exists and parses cleanly, reuse it.
      3. Else try common Steam install paths.
      4. Else prompt the developer.

    Validates the local Mono/Managed baseline and required Phase 1.3 DLLs.
    Writes build/local.props from build/local.props.example with local paths
    substituted.

    This script never copies any game DLLs and never modifies anything
    outside this repository except build/local.props.

.PARAMETER AdofaiDir
    Path to the ADOFAI install root (the folder containing
    "A Dance of Fire and Ice.exe").

.PARAMETER UmmDir
    Path to the UnityModManager directory containing UnityModManager.dll and
    0Harmony.dll. Defaults to Managed\UnityModManager under the ADOFAI install.

.PARAMETER NonInteractive
    Skip the interactive prompt. Fails if a path cannot be auto-detected.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-references.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-references.ps1 -AdofaiDir "D:\Games\ADOFAI" -UmmDir "D:\Games\ADOFAI\A Dance of Fire and Ice_Data\Managed\UnityModManager"
#>

[CmdletBinding()]
param(
    [string]$AdofaiDir,
    [string]$UmmDir,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

$baselineAdofaiVersion = '3.1.2'
$baselineUnityVersion = '6000.3.10f1'
$baselineUmmVersion = '0.32.5'
$baselineHarmonyVersion = '2.3.6.0'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$buildDir     = Join-Path $repoRoot 'build'
$exampleProps = Join-Path $buildDir 'local.props.example'
$localProps   = Join-Path $buildDir 'local.props'

function Write-Section($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)      { Write-Host "  + $msg" -ForegroundColor Green }
function Write-Warn($msg)    { Write-Host "  ! $msg" -ForegroundColor Yellow }
function Write-Fail($msg)    { Write-Host "  x $msg" -ForegroundColor Red }
function Write-Info($msg)    { Write-Host "  - $msg" -ForegroundColor Gray }

function Get-FileMetadata([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $assemblyVersion = $null
    try {
        $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()
    } catch {
        $assemblyVersion = 'n/a'
    }

    [pscustomobject]@{
        Path = $path
        FileVersion = $versionInfo.FileVersion
        ProductVersion = $versionInfo.ProductVersion
        AssemblyVersion = $assemblyVersion
    }
}

function Format-VersionValue([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return 'n/a' }
    return $value
}

function Write-FileMetadata([string]$name, [string]$path) {
    $metadata = Get-FileMetadata $path
    if (-not $metadata) { return }

    Write-Info "$name path: $($metadata.Path)"
    Write-Info "$name FileVersion: $(Format-VersionValue $metadata.FileVersion)"
    Write-Info "$name AssemblyVersion: $(Format-VersionValue $metadata.AssemblyVersion)"
    Write-Info "$name ProductVersion: $(Format-VersionValue $metadata.ProductVersion)"
}

function Test-RequiredFile([string]$name, [string]$path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Write-Ok $name
        Write-FileMetadata $name $path
        return $true
    }

    Write-Fail "$name missing (expected at: $path)"
    return $false
}

function Test-BaselineVersion([string]$name, [string]$path, [string]$expectedVersion) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Fail "$name missing (expected at: $path)"
        return $false
    }

    $metadata = Get-FileMetadata $path
    $actualVersion = $metadata.FileVersion
    Write-Ok $name
    Write-FileMetadata $name $path

    if ($actualVersion -eq $expectedVersion) {
        Write-Ok "$name matches expected baseline $expectedVersion"
    } else {
        Write-Warn "$name baseline differs: expected $expectedVersion, detected $(Format-VersionValue $actualVersion). Runtime validation is required."
    }

    return $true
}

function Test-AdofaiRoot([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $false }
    $exe = Join-Path $path 'A Dance of Fire and Ice.exe'
    return Test-Path -LiteralPath $exe -PathType Leaf
}

function Read-ExistingLocalProps([string]$propsPath) {
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { return $null }
    try {
        [xml]$xml = Get-Content -LiteralPath $propsPath -Raw
        $group = $xml.Project.PropertyGroup
        return [pscustomobject]@{
            AdofaiInstallDir = [string]$group.AdofaiInstallDir
            AdofaiUmmDir = [string]$group.AdofaiUmmDir
        }
    } catch {
        Write-Warn "Failed to parse existing $propsPath : $($_.Exception.Message)"
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
                Write-Warn "Failed to parse $vdf : $($_.Exception.Message)"
            }
        }
    }

    return ($candidates | Select-Object -Unique)
}

Write-Section 'Reference baseline'
Write-Info "ADOFAI baseline: v$baselineAdofaiVersion"
Write-Info "Unity baseline: $baselineUnityVersion"
Write-Info "Unity Mod Manager baseline: $baselineUmmVersion"
Write-Info "Harmony baseline: $baselineHarmonyVersion"

Write-Section 'Resolving ADOFAI install directory'

$resolved = $null
$existingProps = Read-ExistingLocalProps $localProps

if ($AdofaiDir) {
    if (Test-AdofaiRoot $AdofaiDir) {
        $resolved = (Resolve-Path -LiteralPath $AdofaiDir).Path
        Write-Ok "Using -AdofaiDir: $resolved"
    } else {
        Write-Fail "-AdofaiDir was supplied but does not look like an ADOFAI install root: $AdofaiDir"
        exit 1
    }
}

if (-not $resolved -and $existingProps -and (Test-AdofaiRoot $existingProps.AdofaiInstallDir)) {
    $resolved = (Resolve-Path -LiteralPath $existingProps.AdofaiInstallDir).Path
    Write-Ok "Reusing existing build/local.props: $resolved"
} elseif (-not $resolved -and $existingProps -and $existingProps.AdofaiInstallDir) {
    Write-Warn "Existing build/local.props points at an invalid path: $($existingProps.AdofaiInstallDir)"
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
    Write-Warn 'Could not auto-detect ADOFAI install.'
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
$defaultUmmDir = Join-Path $managedDir 'UnityModManager'
$resolvedUmmDir = $defaultUmmDir

Write-Section 'Resolving Unity Mod Manager directory'

if ($UmmDir) {
    if (Test-Path -LiteralPath $UmmDir -PathType Container) {
        $resolvedUmmDir = (Resolve-Path -LiteralPath $UmmDir).Path
        Write-Ok "Using -UmmDir: $resolvedUmmDir"
    } else {
        Write-Fail "-UmmDir was supplied but does not exist: $UmmDir"
        exit 1
    }
} elseif ($existingProps -and $existingProps.AdofaiUmmDir -and (Test-Path -LiteralPath $existingProps.AdofaiUmmDir -PathType Container)) {
    $resolvedUmmDir = (Resolve-Path -LiteralPath $existingProps.AdofaiUmmDir).Path
    Write-Ok "Reusing existing AdofaiUmmDir: $resolvedUmmDir"
} else {
    Write-Ok "Using default UMM directory: $resolvedUmmDir"
}

$monoDir = Join-Path $resolved 'MonoBleedingEdge'
$gameAssembly = Join-Path $resolved 'GameAssembly.dll'

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

if (Test-Path -LiteralPath $resolvedUmmDir -PathType Container) {
    Write-Ok "UMM directory: $resolvedUmmDir"
} else {
    Write-Fail "UMM directory missing: $resolvedUmmDir"
    $missing++
}

if (Test-Path -LiteralPath $monoDir -PathType Container) {
    Write-Ok 'MonoBleedingEdge/ present (Mono/Managed baseline)'
} else {
    Write-Fail "MonoBleedingEdge/ missing (expected at: $monoDir)"
    $missing++
}

if (Test-Path -LiteralPath $gameAssembly -PathType Leaf) {
    Write-Warn "GameAssembly.dll present — IL2CPP risk; current baseline expects Mono/Managed. Runtime/API validation is required."
} else {
    Write-Ok 'GameAssembly.dll not present (Mono/Managed baseline)'
}

Write-Section 'Validating Phase 2.0 compile-time DLLs'

if (-not (Test-BaselineVersion 'UnityModManager.dll' (Join-Path $resolvedUmmDir 'UnityModManager.dll') $baselineUmmVersion)) { $missing++ }
if (-not (Test-BaselineVersion '0Harmony.dll' (Join-Path $resolvedUmmDir '0Harmony.dll') $baselineHarmonyVersion)) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.CoreModule.dll' (Join-Path $managedDir 'UnityEngine.CoreModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.IMGUIModule.dll' (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'))) { $missing++ }
# Phase 2.0: ScreenCapture.CaptureScreenshot is the screenshot API. Required.
if (-not (Test-RequiredFile 'UnityEngine.ScreenCaptureModule.dll' (Join-Path $managedDir 'UnityEngine.ScreenCaptureModule.dll'))) { $missing++ }
# Phase 2.0: legacy Input.GetKeyDown / KeyCode for F9 / F10 hotkeys. Required.
if (-not (Test-RequiredFile 'UnityEngine.InputLegacyModule.dll' (Join-Path $managedDir 'UnityEngine.InputLegacyModule.dll'))) { $missing++ }

Write-Section 'Optional / future DLL checks'

$unityUmbrella = Join-Path $managedDir 'UnityEngine.dll'
if (Test-Path -LiteralPath $unityUmbrella -PathType Leaf) {
    Write-Ok 'UnityEngine.dll (legacy umbrella, present; csproj references it only when present; NOT used as a fallback for Input / ScreenCapture)'
    Write-FileMetadata 'UnityEngine.dll' $unityUmbrella
} else {
    Write-Warn 'UnityEngine.dll not present. Unity 6000 may not ship this legacy umbrella DLL; Phase 2.0 does not require it.'
}

Write-Section 'Phase 4 candidate checks (informational only)'
$assemblyCSharp = Join-Path $managedDir 'Assembly-CSharp.dll'
if (Test-Path -LiteralPath $assemblyCSharp -PathType Leaf) {
    Write-Ok 'Assembly-CSharp.dll (candidate for later Phase 4 analysis only; not referenced by Phase 2.0 build)'
    Write-FileMetadata 'Assembly-CSharp.dll' $assemblyCSharp
} else {
    Write-Warn 'Assembly-CSharp.dll not present; Phase 2.0 will continue because it is not a compile-time reference.'
}

$firstpass = Join-Path $managedDir 'Assembly-CSharp-firstpass.dll'
if (Test-Path -LiteralPath $firstpass -PathType Leaf) {
    Write-Ok 'Assembly-CSharp-firstpass.dll (candidate for later analysis only)'
    Write-FileMetadata 'Assembly-CSharp-firstpass.dll' $firstpass
} else {
    Write-Warn 'Assembly-CSharp-firstpass.dll not present; informational only.'
}

if ($missing -gt 0) {
    Write-Fail "$missing required baseline item(s) missing. Aborting without writing build/local.props."
    exit 1
}

Write-Section 'Writing build/local.props'

if (-not (Test-Path -LiteralPath $exampleProps -PathType Leaf)) {
    Write-Fail "Template not found: $exampleProps"
    exit 1
}

$template = Get-Content -LiteralPath $exampleProps -Raw
$rendered = [regex]::Replace(
    $template,
    '(?s)<AdofaiInstallDir>.*?</AdofaiInstallDir>',
    "<AdofaiInstallDir>$resolved</AdofaiInstallDir>"
)
$rendered = [regex]::Replace(
    $rendered,
    '(?s)<AdofaiUmmDir>.*?</AdofaiUmmDir>',
    "<AdofaiUmmDir>$resolvedUmmDir</AdofaiUmmDir>"
)

if (-not (Test-Path -LiteralPath $buildDir -PathType Container)) {
    New-Item -ItemType Directory -Path $buildDir | Out-Null
}

Set-Content -LiteralPath $localProps -Value $rendered -Encoding UTF8
Write-Ok "Wrote $localProps"

Write-Section 'Done.'
Write-Host "Next step: dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release" -ForegroundColor Cyan
