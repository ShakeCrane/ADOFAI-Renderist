<#
.SYNOPSIS
    Generates build/local.props pointing at the local ADOFAI install and
    verifies the supported ADOFAI / Unity / UMM reference baseline.

.DESCRIPTION
    Strategy:
      1. If -AdofaiDir was supplied, use it.
      2. Else if build/local.props already exists and parses cleanly, reuse it.
      3. Else try common Steam install paths.
      4. Else prompt the developer.

    The project supports only the latest validated ADOFAI public Steam build.
    This script validates the known Assembly-CSharp file version and, when a
    Steam appmanifest is available, the exact public-branch Steam buildid.

    The script never copies game DLLs and never modifies anything outside this
    repository except build/local.props.
#>

[CmdletBinding()]
param(
    [string]$AdofaiDir,
    [string]$UmmDir,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

# Current public Steam branch baseline verified on 2026-09-10.
$baselineSteamAppId = '977950'
$baselineSteamBuildId = '24397494'
$baselineAssemblyCSharpFileVersion = '0.4.3.0'
$baselineUnityVersion = '6000.3.10f1'
$baselineUmmVersion = '0.33.0'
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
    return Test-Path -LiteralPath (Join-Path $path 'A Dance of Fire and Ice.exe') -PathType Leaf
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
        return $null
    }
}

function Find-AdofaiCandidates {
    $candidates = New-Object System.Collections.Generic.List[string]
    $relative = 'steamapps\common\A Dance of Fire and Ice'

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
        $candidates.Add((Join-Path $root $relative))
    }

    foreach ($root in $steamRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        try {
            $content = Get-Content -LiteralPath $vdf -Raw
            foreach ($match in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
                $library = $match.Groups[1].Value -replace '\\\\','\'
                $candidates.Add((Join-Path $library $relative))
            }
        } catch {
            Write-Warn "Failed to parse $vdf : $($_.Exception.Message)"
        }
    }

    return ($candidates | Select-Object -Unique)
}

function Find-SteamAppManifest([string]$installDir) {
    try {
        $commonDir = Split-Path -Parent $installDir
        if ((Split-Path -Leaf $commonDir) -ine 'common') { return $null }
        $steamappsDir = Split-Path -Parent $commonDir
        $manifest = Join-Path $steamappsDir ("appmanifest_{0}.acf" -f $baselineSteamAppId)
        if (Test-Path -LiteralPath $manifest -PathType Leaf) { return $manifest }
    } catch {
    }
    return $null
}

function Read-SteamBuildId([string]$manifestPath) {
    if ([string]::IsNullOrWhiteSpace($manifestPath)) { return $null }
    try {
        $content = Get-Content -LiteralPath $manifestPath -Raw
        $match = [regex]::Match($content, '"buildid"\s+"([0-9]+)"')
        if ($match.Success) { return $match.Groups[1].Value }
    } catch {
        Write-Warn "Failed to parse Steam appmanifest: $($_.Exception.Message)"
    }
    return $null
}

function Test-SupportedAdofaiBaseline([string]$installDir, [string]$managedDir) {
    Write-Section 'Validating supported ADOFAI release'

    $assemblyCSharp = Join-Path $managedDir 'Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath $assemblyCSharp -PathType Leaf)) {
        Write-Fail 'Assembly-CSharp.dll missing; current reflection baseline cannot be verified.'
        return $false
    }

    $metadata = Get-FileMetadata $assemblyCSharp
    Write-FileMetadata 'Assembly-CSharp.dll' $assemblyCSharp
    if (-not $metadata -or $metadata.FileVersion -ne $baselineAssemblyCSharpFileVersion) {
        Write-Fail "Unsupported ADOFAI Assembly-CSharp baseline: expected FileVersion $baselineAssemblyCSharpFileVersion, detected $(Format-VersionValue $metadata.FileVersion)."
        return $false
    }
    Write-Ok "Assembly-CSharp.dll matches supported baseline $baselineAssemblyCSharpFileVersion"

    $manifest = Find-SteamAppManifest $installDir
    if ($manifest) {
        $buildId = Read-SteamBuildId $manifest
        if ([string]::IsNullOrWhiteSpace($buildId)) {
            Write-Fail "Steam appmanifest found but buildid could not be read: $manifest"
            return $false
        }
        if ($buildId -ne $baselineSteamBuildId) {
            Write-Fail "Unsupported ADOFAI Steam buildid: expected public build $baselineSteamBuildId, detected $buildId."
            return $false
        }
        Write-Ok "ADOFAI Steam buildid matches supported public baseline $baselineSteamBuildId"
    } else {
        Write-Warn 'Steam appmanifest not found; exact Steam build identity cannot be verified. Assembly-CSharp baseline matched, but runtime validation remains required.'
    }

    return $true
}

Write-Section 'Reference baseline'
Write-Info "ADOFAI public Steam buildid: $baselineSteamBuildId"
Write-Info "Assembly-CSharp FileVersion: $baselineAssemblyCSharpFileVersion"
Write-Info "Unity baseline: $baselineUnityVersion"
Write-Info "Unity Mod Manager baseline: $baselineUmmVersion"
Write-Info "Harmony baseline: $baselineHarmonyVersion"

Write-Section 'Resolving ADOFAI install directory'
$resolved = $null
$existingProps = Read-ExistingLocalProps $localProps

if ($AdofaiDir) {
    if (-not (Test-AdofaiRoot $AdofaiDir)) {
        Write-Fail "-AdofaiDir was supplied but does not look like an ADOFAI install root: $AdofaiDir"
        exit 1
    }
    $resolved = (Resolve-Path -LiteralPath $AdofaiDir).Path
    Write-Ok "Using -AdofaiDir: $resolved"
}

if (-not $resolved -and $existingProps -and (Test-AdofaiRoot $existingProps.AdofaiInstallDir)) {
    $resolved = (Resolve-Path -LiteralPath $existingProps.AdofaiInstallDir).Path
    Write-Ok "Reusing existing build/local.props: $resolved"
} elseif (-not $resolved -and $existingProps -and $existingProps.AdofaiInstallDir) {
    Write-Warn "Existing build/local.props points at an invalid path: $($existingProps.AdofaiInstallDir)"
}

if (-not $resolved) {
    foreach ($candidate in Find-AdofaiCandidates) {
        if (Test-AdofaiRoot $candidate) {
            $resolved = (Resolve-Path -LiteralPath $candidate).Path
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
    if (-not (Test-AdofaiRoot $answer)) {
        Write-Fail "Path does not look like an ADOFAI install root: $answer"
        exit 1
    }
    $resolved = (Resolve-Path -LiteralPath $answer).Path
    Write-Ok "Using: $resolved"
}

$managedDir = Join-Path $resolved 'A Dance of Fire and Ice_Data\Managed'
$defaultUmmDir = Join-Path $managedDir 'UnityModManager'
$resolvedUmmDir = $defaultUmmDir

Write-Section 'Resolving Unity Mod Manager directory'
if ($UmmDir) {
    if (-not (Test-Path -LiteralPath $UmmDir -PathType Container)) {
        Write-Fail "-UmmDir was supplied but does not exist: $UmmDir"
        exit 1
    }
    $resolvedUmmDir = (Resolve-Path -LiteralPath $UmmDir).Path
    Write-Ok "Using -UmmDir: $resolvedUmmDir"
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
    if (-not (Test-SupportedAdofaiBaseline $resolved $managedDir)) { $missing++ }
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
    Write-Warn 'GameAssembly.dll present — IL2CPP risk; current baseline expects Mono/Managed. Runtime/API validation is required.'
} else {
    Write-Ok 'GameAssembly.dll not present (Mono/Managed baseline)'
}

Write-Section 'Validating compile-time DLLs'
if (-not (Test-BaselineVersion 'UnityModManager.dll' (Join-Path $resolvedUmmDir 'UnityModManager.dll') $baselineUmmVersion)) { $missing++ }
if (-not (Test-BaselineVersion '0Harmony.dll' (Join-Path $resolvedUmmDir '0Harmony.dll') $baselineHarmonyVersion)) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.CoreModule.dll' (Join-Path $managedDir 'UnityEngine.CoreModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.IMGUIModule.dll' (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'))) { $missing++ }
if (-not (Test-RequiredFile 'UnityEngine.ImageConversionModule.dll' (Join-Path $managedDir 'UnityEngine.ImageConversionModule.dll'))) { $missing++ }

Write-Section 'Optional / informational DLL checks'
$unityUmbrella = Join-Path $managedDir 'UnityEngine.dll'
if (Test-Path -LiteralPath $unityUmbrella -PathType Leaf) {
    Write-Ok 'UnityEngine.dll (legacy umbrella, present; csproj references it only when present)'
    Write-FileMetadata 'UnityEngine.dll' $unityUmbrella
} else {
    Write-Warn 'UnityEngine.dll not present. Unity 6000 may not ship this legacy umbrella DLL; the project does not require it.'
}

$firstpass = Join-Path $managedDir 'Assembly-CSharp-firstpass.dll'
if (Test-Path -LiteralPath $firstpass -PathType Leaf) {
    Write-Ok 'Assembly-CSharp-firstpass.dll (informational only)'
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
