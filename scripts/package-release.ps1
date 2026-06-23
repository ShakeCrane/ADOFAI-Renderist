<#
.SYNOPSIS
    Builds and packages an ADOFAI.Renderist release zip suitable for UMM install.

.DESCRIPTION
    Phase 1.4 release packaging baseline.

    The zip contains exactly three TOP-LEVEL files (no subdirectories):
      * Info.json
      * ADOFAI.Renderist.dll
      * LICENSE

    The script is intentionally narrow:
      * Does not modify any source file. Use scripts/set-version.ps1 for that.
      * Does not deploy to the ADOFAI install. Use scripts/copy-to-mods.ps1 for that.
      * Does not copy PDB / XML / config / runtime cache files into the zip.
      * Refuses to write outside the repository.
      * Refuses to use dangerous output directories
        (src/, mod/, scripts/, build/, references/, .git/, .vscode/,
         or any ADOFAI install directory).
      * Reads the target version from mod/Info.json; an explicit -Version
        argument must match Info.json or the script fails.
      * Cross-checks Info.json Version against csproj <Version>.
      * Default: also runs scripts/verify-release-package.ps1 on the produced zip.
        Use -SkipVerify to disable.
      * Default: also runs `dotnet build -c Release` on the project before
        packaging. Use -SkipBuild to disable.

.PARAMETER Configuration
    Build configuration to package. Default: Release.

.PARAMETER Version
    Optional explicit version. If supplied, must equal mod/Info.json Version.

.PARAMETER OutputDir
    Optional output directory. Default: <repo>/dist. Must remain inside the
    repository and must not be one of the protected paths listed above.

.PARAMETER Clean
    If set, removes any pre-existing zip (and matching .sha256 sidecar) for
    the target version inside OutputDir before packaging.

.PARAMETER Force
    Overwrite an existing zip of the same name. Without -Force or -Clean,
    a pre-existing zip is a hard failure.

.PARAMETER SkipBuild
    Skip the `dotnet build -c Release` step. The built DLL must already exist.

.PARAMETER SkipVerify
    Skip the post-packaging verify-release-package.ps1 invocation.

.PARAMETER WhatIf
    Print actions without performing them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package-release.ps1
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$OutputDir,
    [switch]$Clean,
    [switch]$Force,
    [switch]$SkipBuild,
    [switch]$SkipVerify,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

function Fail($msg) {
    Write-Error $msg
    exit 1
}

# ---------- repo-relative paths ------------------------------------------------

$repoRoot     = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$infoJsonPath = Join-Path $repoRoot 'mod\Info.json'
$csprojPath   = Join-Path $repoRoot 'src\ADOFAI.Renderist\ADOFAI.Renderist.csproj'
$licensePath  = Join-Path $repoRoot 'LICENSE'
$builtDllPath = Join-Path $repoRoot ("src\ADOFAI.Renderist\bin\{0}\ADOFAI.Renderist.dll" -f $Configuration)
$verifyScript = Join-Path $PSScriptRoot 'verify-release-package.ps1'

foreach ($p in @($infoJsonPath, $csprojPath, $licensePath)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
        Fail "Required source file not found: $p"
    }
}

# ---------- 1. read & cross-check versions -----------------------------------

try {
    $infoJson = Get-Content -LiteralPath $infoJsonPath -Raw | ConvertFrom-Json
} catch {
    Fail "Failed to parse mod/Info.json: $($_.Exception.Message)"
}
$infoVersion = [string]$infoJson.Version
if ([string]::IsNullOrWhiteSpace($infoVersion)) {
    Fail "mod/Info.json has no Version."
}

[xml]$csprojXml = Get-Content -LiteralPath $csprojPath -Raw
$csprojVersion = [string]$csprojXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($csprojVersion)) {
    Fail "csproj has no <Version>."
}
if ($infoVersion -ne $csprojVersion) {
    Fail "Version mismatch: Info.json=$infoVersion, csproj=$csprojVersion. Run scripts/set-version.ps1 first."
}

if ($PSBoundParameters.ContainsKey('Version') -and $Version -ne $infoVersion) {
    Fail "Explicit -Version '$Version' does not match mod/Info.json Version '$infoVersion'."
}
$resolvedVersion = $infoVersion

# ---------- 2. resolve & validate output directory ---------------------------

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'dist'
}
try {
    $outputDirAbs = [System.IO.Path]::GetFullPath($OutputDir)
} catch {
    Fail "Invalid OutputDir: $OutputDir"
}

$sep = [System.IO.Path]::DirectorySeparatorChar
$repoRootWithSep = if ($repoRoot.EndsWith($sep)) { $repoRoot } else { $repoRoot + $sep }
if (-not (
        $outputDirAbs.Equals($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outputDirAbs.StartsWith($repoRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)
    )) {
    Fail "OutputDir must live inside the repository. Got: $outputDirAbs"
}
if ($outputDirAbs.Equals($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "OutputDir must not be the repository root."
}

$forbiddenSubdirs = @('src','mod','scripts','build','references','.git','.vscode','.vs','.idea','A Dance of Fire and Ice_Data')
foreach ($sub in $forbiddenSubdirs) {
    $forbiddenAbs = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $sub))
    $forbiddenWithSep = if ($forbiddenAbs.EndsWith($sep)) { $forbiddenAbs } else { $forbiddenAbs + $sep }
    if ($outputDirAbs.Equals($forbiddenAbs, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outputDirAbs.StartsWith($forbiddenWithSep, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "OutputDir resolves into a protected location ('$sub'). Got: $outputDirAbs"
    }
}

# Refuse to write into an ADOFAI install directory (best-effort heuristic).
$gameExeNearby = Join-Path $outputDirAbs 'A Dance of Fire and Ice.exe'
if (Test-Path -LiteralPath $gameExeNearby -PathType Leaf) {
    Fail "OutputDir appears to be an ADOFAI install directory. Refusing: $outputDirAbs"
}

if (-not (Test-Path -LiteralPath $outputDirAbs -PathType Container)) {
    if ($WhatIf) {
        Write-Host "WhatIf: would create $outputDirAbs"
    } else {
        New-Item -ItemType Directory -Path $outputDirAbs -Force | Out-Null
    }
}

$zipName     = "ADOFAI.Renderist-v$resolvedVersion.zip"
$zipPath     = Join-Path $outputDirAbs $zipName
$shaSidecar  = "$zipPath.sha256"

# ---------- 3. optional clean -------------------------------------------------

if ($Clean) {
    foreach ($p in @($zipPath, $shaSidecar)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            if ($WhatIf) {
                Write-Host "WhatIf: would remove $p"
            } else {
                Remove-Item -LiteralPath $p -Force
                Write-Host "  - removed $p"
            }
        }
    }
}

if ((Test-Path -LiteralPath $zipPath -PathType Leaf) -and -not $Force -and -not $WhatIf) {
    Fail "Output zip already exists: $zipPath. Use -Force or -Clean to overwrite."
}

# ---------- 4. optional build -------------------------------------------------

if (-not $SkipBuild) {
    Write-Host "==> dotnet build -c $Configuration $csprojPath" -ForegroundColor Cyan
    if ($WhatIf) {
        Write-Host "WhatIf: would run dotnet build"
    } else {
        & dotnet build -c $Configuration $csprojPath
        if ($LASTEXITCODE -ne 0) {
            Fail "dotnet build failed with exit code $LASTEXITCODE."
        }
    }
} else {
    Write-Host "==> Skipping build (-SkipBuild)." -ForegroundColor Yellow
}

if (-not (Test-Path -LiteralPath $builtDllPath -PathType Leaf)) {
    Fail "Built DLL not found: $builtDllPath. Run prepare-references.ps1 and/or remove -SkipBuild."
}

# ---------- 5. stage & zip ----------------------------------------------------

$stagingRoot = Join-Path $outputDirAbs (".staging-{0}" -f ([Guid]::NewGuid().ToString('N')))
if ($WhatIf) {
    Write-Host "WhatIf: would create staging $stagingRoot"
} else {
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
}

try {
    $sources = @(
        @{ Src = $infoJsonPath; Name = 'Info.json' },
        @{ Src = $builtDllPath; Name = 'ADOFAI.Renderist.dll' },
        @{ Src = $licensePath;  Name = 'LICENSE' }
    )

    foreach ($item in $sources) {
        $dst = Join-Path $stagingRoot $item.Name
        if ($WhatIf) {
            Write-Host "WhatIf: would copy $($item.Src) -> $dst"
            continue
        }
        Copy-Item -LiteralPath $item.Src -Destination $dst -Force
        $srcHash = (Get-FileHash -LiteralPath $item.Src -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
        if ($srcHash -ne $dstHash) {
            Fail "Hash mismatch after staging $($item.Src)"
        }
        Write-Host "  + staged $($item.Name)  (sha256: $($srcHash.Substring(0,12))…)"
    }

    if ((Test-Path -LiteralPath $zipPath -PathType Leaf) -and ($Force -or $Clean)) {
        if ($WhatIf) {
            Write-Host "WhatIf: would remove existing $zipPath"
        } else {
            Remove-Item -LiteralPath $zipPath -Force
        }
    }

    Write-Host "==> Creating $zipPath" -ForegroundColor Cyan
    if (-not $WhatIf) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        # CreateFromDirectory writes entries relative to the directory root,
        # so the zip ends up with no top-level folder.
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stagingRoot,
            $zipPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false   # includeBaseDirectory = false
        )
    }

    if (-not $WhatIf) {
        $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        Set-Content -LiteralPath $shaSidecar -Value ("{0}  {1}" -f $zipHash, $zipName) -Encoding ASCII
        Write-Host "  + wrote $shaSidecar"
        Write-Host "  zip sha256: $zipHash"
    }
} finally {
    if (-not $WhatIf -and (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

# ---------- 6. post-verify ----------------------------------------------------

if (-not $SkipVerify) {
    if (-not (Test-Path -LiteralPath $verifyScript -PathType Leaf)) {
        Fail "verify-release-package.ps1 not found at $verifyScript"
    }
    Write-Host "==> Verifying $zipPath" -ForegroundColor Cyan
    if ($WhatIf) {
        Write-Host "WhatIf: would run verify-release-package.ps1"
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifyScript -ZipPath $zipPath -ExpectedVersion $resolvedVersion
        if ($LASTEXITCODE -ne 0) {
            Fail "verify-release-package.ps1 failed with exit code $LASTEXITCODE."
        }
    }
} else {
    Write-Host "==> Skipping verify (-SkipVerify)." -ForegroundColor Yellow
}

Write-Host "Done. $zipPath" -ForegroundColor Cyan
