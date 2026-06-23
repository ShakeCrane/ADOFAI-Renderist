# Local Reference Setup

This document explains how to prepare local references so that
`ADOFAI.Renderist.csproj` can resolve ADOFAI / Unity / Unity Mod Manager
assemblies on your machine.

No proprietary DLLs are committed to this repository. Every developer
must supply them from their own ADOFAI installation.

## Current baseline

- Target framework: `net48`
- UMM: `0.32.5` (`UnityModManager.dll` version `0.32.5.0`)
- Harmony: UMM-bundled `0Harmony.dll` version `2.3.6.0`
- Local scripts live in `scripts/`
- `UnityEngine.ScreenCaptureModule.dll` must exist locally and must not be committed

## Required DLLs (Phase 1)

The build will fail if any of these are missing:

| DLL                          | Location (relative to ADOFAI install root)                                  |
| ---------------------------- | --------------------------------------------------------------------------- |
| `UnityModManager.dll`        | `A Dance of Fire and Ice_Data\Managed\UnityModManager\UnityModManager.dll`  |
| `0Harmony.dll`               | `A Dance of Fire and Ice_Data\Managed\UnityModManager\0Harmony.dll`         |
| `UnityEngine.CoreModule.dll` | `A Dance of Fire and Ice_Data\Managed\UnityEngine.CoreModule.dll`           |
| `UnityEngine.IMGUIModule.dll`| `A Dance of Fire and Ice_Data\Managed\UnityEngine.IMGUIModule.dll`          |
| `UnityEngine.ScreenCaptureModule.dll` | `A Dance of Fire and Ice_Data\Managed\UnityEngine.ScreenCaptureModule.dll` |

Soft / optional:

| DLL                | Notes                                                                |
| ------------------ | -------------------------------------------------------------------- |
| `UnityEngine.dll`  | Legacy umbrella assembly. Referenced only if present.                |

Phase 4 will additionally require:

| DLL                              | Notes                                                |
| -------------------------------- | ---------------------------------------------------- |
| `Assembly-CSharp.dll`            | Not used in Phase 1. Will be introduced in Phase 4.  |
| `Assembly-CSharp-firstpass.dll`  | Same as above.                                       |

## Setup steps

### Option A — VSCode task (recommended)

1. Open the repository root in VSCode.
2. Run the task **Prepare References** (from the Command Palette:
   `Tasks: Run Task` → `Prepare References`).
3. The script will:
   - Try the Steam default path, then prompt you if not found.
   - Verify that the required DLLs exist.
   - Generate `build/local.props` (gitignored) from
     `build/local.props.example`.

### Option B — manual

1. Copy `build/local.props.example` to `build/local.props`.
2. Edit `AdofaiInstallDir` to point at your local ADOFAI root, e.g.
   `D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice`.
3. Save. Done.

## How MSBuild finds these paths

When you run `dotnet build`, MSBuild walks up from the `.csproj` file and
automatically imports the nearest `Directory.Build.props`. The repo-root
`Directory.Build.props` then imports `build\local.props` if it exists,
exposing `$(AdofaiInstallDir)`, `$(AdofaiManagedDir)`, `$(AdofaiUmmDir)`
to every `.csproj` in the repo.

## Hygiene

- Never commit `build/local.props` — it is gitignored.
- Never commit anything under `references/<subdir>/` — those folders are
  gitignored and are reserved for optional local copies of DLLs.
- Never commit ADOFAI / Unity / UMM DLLs in any form.

## Resetting

Run **Clean References** (VSCode task) or
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/clean-references.ps1`
to remove `build/local.props` and any locally cached references.
