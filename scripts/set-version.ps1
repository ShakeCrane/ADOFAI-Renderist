<#
.SYNOPSIS
    Synchronizes the ADOFAI.Renderist product version and phase text.

.DESCRIPTION
    Atomically updates every file that carries an authoritative product version or
    product-identity phase string:

      * mod/Info.json Version
      * src/ADOFAI.Renderist/ADOFAI.Renderist.csproj <Version>
      * src/ADOFAI.Renderist/ModEntry.cs ModVersion constant
      * src/ADOFAI.Renderist/ModEntry.cs load log "Loaded ADOFAI Renderist <version> (<phase>)."
      * src/ADOFAI.Renderist/ModEntry.cs product-identity phase summary comment ("/// Phase ...")
      * src/ADOFAI.Renderist/Export/EditorExportSession.cs PhaseLabel

    Every edit is a unique-match replacement: if a pattern does not match exactly once
    the script fails without writing anything. It never performs a fuzzy global replace
    and never rewrites feature-provenance comments (for example "Phase 3.8.0: FFmpeg
    组件管理"), which record when a feature was introduced rather than the current phase.

    The script does not modify README.md, AGENTS.md, bin/, obj/, or game files.

.PARAMETER Version
    Required four-part version number, for example: 0.3.3.1.

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
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
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
$editorExportSessionPath = Join-Path $repoRoot 'src\ADOFAI.Renderist\Export\EditorExportSession.cs'

$targetPaths = @($infoJsonPath, $csprojPath, $modEntryPath, $editorExportSessionPath)
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

function Assert-Contains([string]$path, [string]$needle, [string]$description) {
    $content = Read-TextFile $path
    if (-not $content.Contains($needle)) {
        Fail "$description not found in $path (expected: $needle)"
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
    $oldLogPhase = Get-UniqueValue $originals[$modEntryPath] 'Loaded ADOFAI Renderist [0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)? \(([^)]+)\)\.' 'ModEntry load phase'
    $oldModVersion = Get-UniqueValue $originals[$modEntryPath] 'internal const string ModVersion = "([^"]+)"' 'ModEntry ModVersion constant'
    $oldIdentityCommentPhase = Get-UniqueValue $originals[$modEntryPath] '(?m)^[ \t]*/// (Phase [^\r\n]+)' 'ModEntry product-identity phase comment'
    $oldPhaseLabel = Get-UniqueValue $originals[$editorExportSessionPath] 'private const string PhaseLabel = "([^"]*)"' 'EditorExportSession.PhaseLabel'

    if ($oldVersion -ne $oldCsprojVersion) {
        Fail "Existing version mismatch: Info.json has $oldVersion, csproj has $oldCsprojVersion."
    }
    if ($oldVersion -ne $oldModVersion) {
        Fail "Existing version mismatch: Info.json has $oldVersion, ModEntry.ModVersion has $oldModVersion."
    }
    if ($Phase.EndsWith('.')) {
        Fail "Phase must not end with '.': the ModEntry identity comment pattern already appends one."
    }

    # The identity comment ends with a period; keep that shape and compare phases without it.
    $oldIdentityCommentTerminator = if ($oldIdentityCommentPhase.EndsWith('.')) { '.' } else { '' }
    $oldIdentityCommentValue = $oldIdentityCommentPhase
    if ($oldIdentityCommentTerminator -ne '') {
        $oldIdentityCommentValue = $oldIdentityCommentPhase.Substring(0, $oldIdentityCommentPhase.Length - 1)
    }

    $newInfoJson = Replace-Unique $originals[$infoJsonPath] '("Version"\s*:\s*")[^"]+(")' "`${1}$Version`${2}" 'mod/Info.json Version'
    $newCsproj = Replace-Unique $originals[$csprojPath] '(<Version>)[^<]+(</Version>)' "`${1}$Version`${2}" 'csproj Version'
    $newModEntry = $originals[$modEntryPath]
    $newModEntry = Replace-Unique $newModEntry 'Loaded ADOFAI Renderist [0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)? \([^)]+\)\.' "Loaded ADOFAI Renderist $Version ($Phase)." 'ModEntry load log'
    $newModEntry = Replace-Unique $newModEntry 'internal const string ModVersion = "[^"]+"' "internal const string ModVersion = `"$Version`"" 'ModEntry ModVersion constant'
    $newModEntry = Replace-Unique $newModEntry '(?m)^([ \t]*/// )Phase [^\r\n]+' "`${1}$Phase$oldIdentityCommentTerminator" 'ModEntry product-identity phase comment'
    $newEditorExportSession = Replace-Unique $originals[$editorExportSessionPath] '(private const string PhaseLabel = ")[^"]*(")' "`${1}$Phase`${2}" 'EditorExportSession.PhaseLabel'

    $planned = @(
        [PSCustomObject]@{ Path = $infoJsonPath; Old = $originals[$infoJsonPath]; New = $newInfoJson },
        [PSCustomObject]@{ Path = $csprojPath; Old = $originals[$csprojPath]; New = $newCsproj },
        [PSCustomObject]@{ Path = $modEntryPath; Old = $originals[$modEntryPath]; New = $newModEntry },
        [PSCustomObject]@{ Path = $editorExportSessionPath; Old = $originals[$editorExportSessionPath]; New = $newEditorExportSession }
    )

    Write-Host '==> Planned version update' -ForegroundColor Cyan
    Write-Host "  Version: $oldVersion -> $Version"
    Write-Host "  Phase (load log):      $oldLogPhase -> $Phase"
    Write-Host "  Phase (identity cmt):  $oldIdentityCommentValue -> $Phase"
    Write-Host "  Phase (PhaseLabel):    $oldPhaseLabel -> $Phase"
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

        # 所有旧的权威版本 / Phase 字符串都必须从每一个权威文件里消失。
        # 只过滤“新值等于旧值”的项（合法的 no-op 更新），不做模糊匹配。
        $hardResidualNeedles = @(
            $oldVersion,
            $oldLogPhase,
            $oldIdentityCommentValue,
            $oldPhaseLabel
        ) | Where-Object { -not [string]::IsNullOrEmpty($_) -and $_ -ne $Version -and $_ -ne $Phase } |
            Select-Object -Unique
        foreach ($path in $targetPaths) {
            Assert-NoResidual $path $hardResidualNeedles
        }

        # 明确核对：每个权威字符串的新值都真实存在于对应文件中。
        Assert-Contains $infoJsonPath "`"Version`": `"$Version`"" 'mod/Info.json Version'
        Assert-Contains $csprojPath "<Version>$Version</Version>" 'csproj Version'
        Assert-Contains $modEntryPath "internal const string ModVersion = `"$Version`"" 'ModEntry ModVersion'
        Assert-Contains $modEntryPath "Loaded ADOFAI Renderist $Version ($Phase)." 'ModEntry load log'
        Assert-Contains $modEntryPath "/// $Phase" 'ModEntry identity comment'
        Assert-Contains $editorExportSessionPath "private const string PhaseLabel = `"$Phase`";" 'EditorExportSession.PhaseLabel'

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
