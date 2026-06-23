<#
.SYNOPSIS
    Removes locally generated reference configuration.

.DESCRIPTION
    Deletes:
      - build/local.props
      - local files under references/ADOFAI, references/Unity, references/UMM,
        references/Mods, references/Decompiled (if those folders exist)

    Preserves .gitkeep placeholders and never touches anything outside the repository.
#>

[CmdletBinding()]
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$localProps = Join-Path $repoRoot 'build\local.props'
$refsRoot   = Join-Path $repoRoot 'references'

function Remove-IfExists([string]$path) {
    if (Test-Path -LiteralPath $path) {
        if ($WhatIf) {
            Write-Host "WhatIf: would remove $path"
        } else {
            Remove-Item -LiteralPath $path -Recurse -Force
            Write-Host "Removed $path"
        }
    } else {
        Write-Host "Skip (not present): $path"
    }
}

function Clear-ReferenceDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        Write-Host "Skip (not present): $path"
        return
    }

    $items = Get-ChildItem -LiteralPath $path -Force | Where-Object { $_.Name -ne '.gitkeep' }
    foreach ($item in $items) {
        if ($WhatIf) {
            Write-Host "WhatIf: would remove $($item.FullName)"
        } else {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
            Write-Host "Removed $($item.FullName)"
        }
    }

    $gitkeep = Join-Path $path '.gitkeep'
    if (Test-Path -LiteralPath $gitkeep -PathType Leaf) {
        Write-Host "Preserved $gitkeep"
    }
}

Remove-IfExists $localProps

foreach ($sub in 'ADOFAI','Unity','UMM','Mods','Decompiled') {
    $p = Join-Path $refsRoot $sub
    Clear-ReferenceDirectory $p
}

Write-Host 'Done.' -ForegroundColor Cyan
