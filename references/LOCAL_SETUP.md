# Local Reference Setup

This document explains how to prepare local references so that
`ADOFAI.Renderist.csproj` can resolve ADOFAI / Unity / Unity Mod Manager
assemblies on your machine.

No proprietary DLLs are committed to this repository. Every developer must
supply them from their own local ADOFAI and UMM installation.

## Current Phase 1.3 baseline

- Phase: `Phase 1.3 local reference baseline`
- ADOFAI baseline: `v3.1.2`
- Unity baseline: `6000.3.10f1`
- Target framework: `net48`
- Unity Mod Manager baseline: `0.32.5` (`UnityModManager.dll` file version `0.32.5`, assembly version `0.32.5.0`)
- Harmony baseline: UMM-bundled `0Harmony.dll` file version `2.3.6.0`

UMM / Harmony version differences are reported as warnings by
`scripts/prepare-references.ps1`. Missing UMM / Harmony DLLs are errors.
If the detected versions differ from the baseline, run an ADOFAI runtime load
check before treating the environment as verified.

## Reference directories

Tracked placeholders:

```text
references/
  LOCAL_SETUP.md
  ADOFAI/
    .gitkeep
  Unity/
    .gitkeep
  UMM/
    .gitkeep
```

These folders are reserved for local reference inspection or caches only. The
Phase 1.3 script does not copy DLLs into them. DLLs, PDBs, XML documentation,
and other local binary/reference artifacts under `references/` must not be
committed.

`references/Mods/` and `references/Decompiled/` are intentionally not created in
Phase 1.3. Mods compatibility remains a later replay/autoplay phase, and local
decompilation remains a later game API analysis phase.

## Required compile-time DLLs for Phase 1.3

The build will fail if any of these are missing:

| DLL | Source | Location |
| --- | --- | --- |
| `UnityModManager.dll` | UMM | `A Dance of Fire and Ice_Data\Managed\UnityModManager\UnityModManager.dll`, or explicit `-UmmDir` |
| `0Harmony.dll` | UMM | `A Dance of Fire and Ice_Data\Managed\UnityModManager\0Harmony.dll`, or explicit `-UmmDir` |
| `UnityEngine.CoreModule.dll` | ADOFAI Managed | `A Dance of Fire and Ice_Data\Managed\UnityEngine.CoreModule.dll` |
| `UnityEngine.IMGUIModule.dll` | ADOFAI Managed | `A Dance of Fire and Ice_Data\Managed\UnityEngine.IMGUIModule.dll` |

`UnityEngine.IMGUIModule.dll` is required because the current UMM GUI uses
Unity IMGUI / `GUILayout` in `ModEntry.OnGUI`.

## Optional / informational DLLs

| DLL | Status |
| --- | --- |
| `UnityEngine.dll` | Legacy umbrella assembly. Unity 6000 may not ship it. The project references it only when present. Missing is not a Phase 1.3 error. |
| `UnityEngine.ScreenCaptureModule.dll` | Not required by Phase 1.3 because current source does not use ScreenCapture APIs. Do not keep it as an unused compile reference for a future screenshot phase. |
| `Assembly-CSharp.dll` | Candidate for later Phase 4 local analysis only. Not a Phase 1.3 compile reference and not a Phase 1.3 success condition. |
| `Assembly-CSharp-firstpass.dll` | Informational only if present. |

## Setup steps

### Option A — prepare script

Run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-references.ps1
```

To use explicit paths:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-references.ps1 `
  -AdofaiDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice" `
  -UmmDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\UnityModManager"
```

The script will:

- resolve the ADOFAI install directory;
- resolve the UMM directory;
- check the Managed directory;
- check the Mono / IL2CPP indicators;
- check required Phase 1.3 compile-time DLLs;
- print DLL path, FileVersion, AssemblyVersion, and ProductVersion where available;
- warn on baseline version differences;
- generate `build/local.props` from `build/local.props.example`.

The script does not copy DLLs and does not modify the game directory.

### Option B — manual

1. Copy `build/local.props.example` to `build/local.props`.
2. Edit `AdofaiInstallDir` to point at your local ADOFAI root, e.g.
   `D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice`.
3. If UMM is not under `A Dance of Fire and Ice_Data\Managed\UnityModManager`,
   edit `AdofaiUmmDir` to point at the actual UMM directory.
4. Save the file. It is gitignored and must not be committed.

## How MSBuild finds these paths

When you run `dotnet build`, MSBuild walks up from the `.csproj` file and
automatically imports the nearest `Directory.Build.props`. The repo-root
`Directory.Build.props` then imports `build\local.props` if it exists, exposing
`$(AdofaiInstallDir)`, `$(AdofaiManagedDir)`, and `$(AdofaiUmmDir)` to every
`.csproj` in the repo.

## Hygiene

- Never commit `build/local.props`.
- Never commit ADOFAI / Unity / UMM DLLs in any form.
- Never commit DLL, PDB, XML documentation, or local cache files under
  `references/`.
- If local DLL versions change, re-run `scripts/prepare-references.ps1` and
  repeat build/runtime validation.
- Do not modify the original ADOFAI install directory.

## Resetting

Run **Clean References** (VSCode task) or:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\clean-references.ps1
```

This removes `build/local.props` and local cached files under `references/`
while preserving tracked `.gitkeep` placeholders.
