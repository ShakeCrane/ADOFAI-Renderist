<#
.SYNOPSIS
    Removes locally generated reference configuration.

.DESCRIPTION
    Deletes:
      - build/local.props
      - everything under references/ADOFAI, references/Unity, references/UMM,
        references/Mods, references/Decompiled (if those folders exist)

    Never touches anything outside the repository.
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

Remove-IfExists $localProps

foreach ($sub in 'ADOFAI','Unity','UMM','Mods','Decompiled') {
    $p = Join-Path $refsRoot $sub
    Remove-IfExists $p
}

Write-Host 'Done.' -ForegroundColor Cyan
