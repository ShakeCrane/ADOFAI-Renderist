<#
.SYNOPSIS
    Runs the FFmpeg component-management (Phase 3.8.0, L1) regression tests.

.DESCRIPTION
    Builds and runs tests/ADOFAI.Renderist.FfmpegTests, which compiles the production
    L1 sources from src/ADOFAI.Renderist/Ffmpeg directly (they deliberately have no
    Unity / UMM / Harmony dependency, so no stubs are required).

    The suite installs nothing and downloads nothing: it uses synthetic archives and
    synthetic assets. Downloaded FFmpeg binaries are never committed.

.PARAMETER FfmpegDirs
    Optional list of directories that each contain a real ffmpeg.exe. When supplied,
    the multi-version capability regression runs against every listed binary.
    Without it that single test is reported as SKIP (it is not a failure).

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    pwsh -File tests/run-ffmpeg-tests.ps1

.EXAMPLE
    pwsh -File tests/run-ffmpeg-tests.ps1 -FfmpegDirs 'D:\ffmpeg-9.0.2\bin','D:\ffmpeg-8.1.2\bin'
#>
[CmdletBinding()]
param(
    [string[]]$FfmpegDirs = @(),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'tests\ADOFAI.Renderist.FfmpegTests\ADOFAI.Renderist.FfmpegTests.csproj'
$exePath = Join-Path $repoRoot "tests\ADOFAI.Renderist.FfmpegTests\bin\$Configuration\ADOFAI.Renderist.FfmpegTests.exe"

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    Write-Host "Test project not found: $projectPath" -ForegroundColor Red
    exit 1
}

Write-Host "==> dotnet build -c $Configuration $projectPath" -ForegroundColor Cyan
& dotnet build -c $Configuration $projectPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "Test project build failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Host "Test executable not found: $exePath" -ForegroundColor Red
    exit 1
}

if ($FfmpegDirs.Count -gt 0) {
    $env:RENDERIST_TEST_FFMPEG_DIR = ($FfmpegDirs -join ';')
    Write-Host "==> real-binary capability regression enabled for:" -ForegroundColor Cyan
    foreach ($dir in $FfmpegDirs) { Write-Host "      $dir" }
} else {
    Remove-Item Env:RENDERIST_TEST_FFMPEG_DIR -ErrorAction SilentlyContinue
    Write-Host "==> no -FfmpegDirs supplied: real-binary capability regression will be SKIP." -ForegroundColor Yellow
}

Write-Host "==> $exePath" -ForegroundColor Cyan
& $exePath
$exitCode = $LASTEXITCODE

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "FFmpeg component regression tests: PASS" -ForegroundColor Green
} else {
    Write-Host "FFmpeg component regression tests: FAIL (exit $exitCode)" -ForegroundColor Red
}
exit $exitCode
