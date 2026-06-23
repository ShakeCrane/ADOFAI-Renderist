<#
.SYNOPSIS
    Synchronizes ADOFAI.Renderist version and phase text.

.DESCRIPTION
    Updates only:
      * mod/Info.json Version
      * src/ADOFAI.Renderist/ADOFAI.Renderist.csproj <Version>
      * src/ADOFAI.Renderist/ModEntry.cs visible version / phase strings

    The script does not modify README.md, AGENTS.md, bin/, obj/, or game files.

.PARAMETER Version
    Required three-part version number, for example: 0.1.2.

.PARAMETER Phase
    Required non-empty phase label, for example: Phase 1.2 toolchain fixes.

.PARAMETER DryRun
    Print planned changes without writing files.

.PARAMETER Yes
    Skip the interactive YES confirmation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory=$true)]
    [ValidateNotNullOrEmpty()]
    [string]$Phase,

    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$infoJsonPath = Join-Path $repoRoot 'mod\Info.json'
$csprojPath = Join-Path $repoRoot 'src\ADOFAI.Renderist\ADOFAI.Renderist.csproj'
$modEntryPath = Join-Path $repoRoot 'src\ADOFAI.Renderist\ModEntry.cs'

$targetPaths = @($infoJsonPath, $csprojPath, $modEntryPath)
$emDash = [char]0x2014
$guiLabelPrefix = "ADOFAI Renderist $emDash "
$guiLabelPrefixPattern = [regex]::Escape($guiLabelPrefix)

function Fail($msg) {
    throw $msg
}

function Assert-FileExists([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "Required file not found: $path"
    }
}

function Read-TextFile([string]$path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

function Write-TextFile([string]$path, [string]$content) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

function Replace-Unique([string]$content, [string]$pattern, [string]$replacement, [string]$description) {
    $matches = [regex]::Matches($content, $pattern)
    if ($matches.Count -ne 1) {
        Fail "$description must match exactly once, matched $($matches.Count)."
    }
    return [regex]::Replace($content, $pattern, $replacement, 1)
}

function Get-UniqueValue([string]$content, [string]$pattern, [string]$description) {
    $matches = [regex]::Matches($content, $pattern)
    if ($matches.Count -ne 1) {
        Fail "$description must match exactly once, matched $($matches.Count)."
    }
    return $matches[0].Groups[1].Value
}

function Restore-Originals($originals, $writtenPaths) {
    foreach ($path in $writtenPaths) {
        if ($originals.ContainsKey($path)) {
            Write-TextFile $path $originals[$path]
        }
    }
}

function Assert-NoResidual([string]$path, [string[]]$needles) {
    $content = Read-TextFile $path
    foreach ($needle in $needles) {
        if (-not [string]::IsNullOrEmpty($needle) -and $content.Contains($needle)) {
            Fail "Residual '$needle' found in $path"
        }
    }
}

function Get-RepoRelativePath([string]$path) {
    $root = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $full = [System.IO.Path]::GetFullPath($path)
    if ($full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length + 1)
    }
    return $full
}

try {
    foreach ($path in $targetPaths) {
        Assert-FileExists $path
    }

    $originals = @{}
    foreach ($path in $targetPaths) {
        $originals[$path] = Read-TextFile $path
    }

    $oldVersion = Get-UniqueValue $originals[$infoJsonPath] '"Version"\s*:\s*"([^"]+)"' 'mod/Info.json Version'
    $oldCsprojVersion = Get-UniqueValue $originals[$csprojPath] '<Version>([^<]+)</Version>' 'csproj Version'
    $oldLogPhase = Get-UniqueValue $originals[$modEntryPath] 'Loaded ADOFAI Renderist [0-9]+\.[0-9]+\.[0-9]+ \(([^)]+)\)\.' 'ModEntry load phase'
    $oldGuiPhase = Get-UniqueValue $originals[$modEntryPath] "GUILayout\.Label\(`"$guiLabelPrefixPattern([^`"]+)`"" 'ModEntry GUI phase'

    if ($oldVersion -ne $oldCsprojVersion) {
        Fail "Existing version mismatch: Info.json has $oldVersion, csproj has $oldCsprojVersion."
    }
    if ($oldLogPhase -ne $oldGuiPhase) {
        Fail "Existing phase mismatch: load log has '$oldLogPhase', GUI has '$oldGuiPhase'."
    }

    $newInfoJson = Replace-Unique $originals[$infoJsonPath] '("Version"\s*:\s*")[^"]+(")' "`${1}$Version`${2}" 'mod/Info.json Version'
    $newCsproj = Replace-Unique $originals[$csprojPath] '(<Version>)[^<]+(</Version>)' "`${1}$Version`${2}" 'csproj Version'
    $newModEntry = $originals[$modEntryPath]
    $newModEntry = Replace-Unique $newModEntry 'Loaded ADOFAI Renderist [0-9]+\.[0-9]+\.[0-9]+ \([^)]+\)\.' "Loaded ADOFAI Renderist $Version ($Phase)." 'ModEntry load log'
    $newModEntry = Replace-Unique $newModEntry "(GUILayout\.Label\(`")$guiLabelPrefixPattern[^`"]+(`")" "`${1}$guiLabelPrefix$Phase`${2}" 'ModEntry GUI label'

    $planned = @(
        [PSCustomObject]@{ Path = $infoJsonPath; Old = $originals[$infoJsonPath]; New = $newInfoJson },
        [PSCustomObject]@{ Path = $csprojPath; Old = $originals[$csprojPath]; New = $newCsproj },
        [PSCustomObject]@{ Path = $modEntryPath; Old = $originals[$modEntryPath]; New = $newModEntry }
    )

    Write-Host '==> Planned version update' -ForegroundColor Cyan
    Write-Host "  Version: $oldVersion -> $Version"
    Write-Host "  Phase: $oldLogPhase -> $Phase"
    Write-Host ''
    Write-Host 'Files:'
    foreach ($item in $planned) {
        $relative = Get-RepoRelativePath $item.Path
        if ($item.Old -eq $item.New) {
            Write-Host "  = $relative"
        } else {
            Write-Host "  * $relative"
        }
    }

    if ($DryRun) {
        Write-Host 'DryRun: no files were modified.' -ForegroundColor Cyan
        return
    }

    if (-not $Yes) {
        $answer = Read-Host 'Apply these version changes? Type YES to continue'
        if ($answer -ne 'YES') {
            Fail 'Aborted by user.'
        }
    }

    $writtenPaths = @()
    try {
        foreach ($item in $planned) {
            if ($item.Old -ne $item.New) {
                Write-TextFile $item.Path $item.New
                $writtenPaths += $item.Path
            }
        }

        Read-TextFile $infoJsonPath | ConvertFrom-Json | Out-Null

        $hardResidualNeedles = @($oldVersion, $oldLogPhase) | Where-Object { $_ -ne $Version -and $_ -ne $Phase }
        foreach ($path in $targetPaths) {
            Assert-NoResidual $path $hardResidualNeedles
        }

        $scriptsPath = Join-Path $repoRoot 'scripts'
        $scriptResiduals = @()
        Get-ChildItem -LiteralPath $scriptsPath -File -Filter '*.ps1' | ForEach-Object {
            if ($_.FullName -eq $PSCommandPath) {
                return
            }
            $content = Read-TextFile $_.FullName
            foreach ($needle in $hardResidualNeedles) {
                if (-not [string]::IsNullOrEmpty($needle) -and $content.Contains($needle)) {
                    $scriptResiduals += "$($_.FullName): $needle"
                }
            }
        }
        if ($scriptResiduals.Count -gt 0) {
            Write-Warning 'Residual old version/phase text found in scripts:'
            $scriptResiduals | ForEach-Object { Write-Warning "  $_" }
        }
    } catch {
        Restore-Originals $originals $writtenPaths
        Fail "Version update failed and written files were rolled back. $($_.Exception.Message)"
    }

    Write-Host 'Done.' -ForegroundColor Cyan
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
