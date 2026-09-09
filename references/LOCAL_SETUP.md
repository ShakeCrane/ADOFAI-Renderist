# Local Reference Setup

This document explains how to prepare local references so that
`ADOFAI.Renderist.csproj` can resolve ADOFAI / Unity / Unity Mod Manager
assemblies on your machine.

No proprietary DLLs are committed to this repository. Every developer must
supply them from their own local ADOFAI and UMM installation.

## Current Phase 3.3.0 baseline

- Phase: `Phase 3.3.0 deterministic hardening`
- Version example (four-part): `0.3.3.1`
- ADOFAI baseline: `v3.3.1`
- Unity baseline: `6000.3.10f1`
- Target framework: `net48`
- Unity Mod Manager baseline: `0.33.0` (`UnityModManager.dll` file version `0.33.0`, assembly version `0.33.0.0`)
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
script does not copy DLLs into them. DLLs, PDBs, XML documentation, and other
local binary/reference artifacts under `references/` must not be committed.

## Required compile-time DLLs

The build will fail if any of these are missing:

| DLL | Source | Location |
| --- | --- | --- |
| `UnityModManager.dll` | UMM | `A Dance of Fire and Ice_Data\Managed\UnityModManager\UnityModManager.dll`, or explicit `-UmmDir` |
| `0Harmony.dll` | UMM | `A Dance of Fire and Ice_Data\Managed\UnityModManager\0Harmony.dll`, or explicit `-UmmDir` |
| `UnityEngine.CoreModule.dll` | ADOFAI Managed | `A Dance of Fire and Ice_Data\Managed\UnityEngine.CoreModule.dll` |
| `UnityEngine.IMGUIModule.dll` | ADOFAI Managed | `A Dance of Fire and Ice_Data\Managed\UnityEngine.IMGUIModule.dll` (UMM GUI uses `GUILayout`) |
| `UnityEngine.ImageConversionModule.dll` | ADOFAI Managed | `A Dance of Fire and Ice_Data\Managed\UnityEngine.ImageConversionModule.dll` (`Texture2D.EncodeToPNG`, the deterministic PNG readback path) |

## Optional / informational DLLs

| DLL | Status |
| --- | --- |
| `UnityEngine.dll` | Legacy umbrella assembly. Unity 6000 may not ship it. The project references it only when present. Missing is not an error. |
| `Assembly-CSharp.dll` | Runtime analysis / reflection source for ADOFAI internal APIs. **Not** a compile-time reference and **not** committed. |
| `Assembly-CSharp-firstpass.dll` | Informational only if present. |

ADOFAI internal APIs are resolved at runtime via `EditorGameReflection` /
`RenderistAutoPlay`; `Assembly-CSharp.dll` is deliberately never referenced
by the build.

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
- check required compile-time DLLs;
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

## Release packaging

The output is a UMM-ready zip placed under `dist/` (gitignored) with a fixed
name:

```text
dist/ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

The packaging scripts do not modify game files and do not deploy to the
ADOFAI install. Use `scripts/copy-to-mods.ps1` for local deployment.

### Build and package

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-references.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

`scripts/package-release.ps1` by default:

- runs `dotnet build -c Release src/ADOFAI.Renderist/ADOFAI.Renderist.csproj`;
- reads the target version from `mod/Info.json`;
- cross-checks against the csproj `<Version>`;
- stages `Info.json`, `ADOFAI.Renderist.dll`, and `LICENSE`;
- writes `dist/ADOFAI.Renderist.zip`;
- writes a `dist/ADOFAI.Renderist.zip.sha256` sidecar (outside the zip);
- invokes `scripts/verify-release-package.ps1` on the output.

Useful flags:

- `-SkipBuild` — assume the Release DLL already exists.
- `-SkipVerify` — do not run the verify script.
- `-Clean` — remove a stale zip and sidecar first.
- `-Force` — overwrite the existing zip.

### Verify an existing zip

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-package.ps1 `
  -ZipPath .\dist\ADOFAI.Renderist.zip
```

The verify script enforces, among other things:

- only the three allowed top-level files exist; no subdirectories;
- no `README*`, `AGENTS.md`, `CLAUDE.md`, `Settings.xml`, `Log.txt`,
  `UnityModManager.dll`, `0Harmony.dll`, `Assembly-CSharp*.dll`,
  `UnityEngine*.dll`, `*.pdb`, `*.xml`, `*.cache`, `*.config`, or UMM
  runtime caches inside the zip;
- `Info.json` fields `Id`, `AssemblyName`, `EntryMethod`, `ManagerVersion`,
  and `Version` match expected values;
- DLL FileVersion or ProductVersion matches `Info.json` Version.

A missing `.sha256` sidecar is not a failure.

### Install for users

In UMM, drag the produced zip into the mod installer, or extract its three
top-level files into `<ADOFAI install>\Mods\ADOFAI.Renderist\` manually.

### Version sync

Always change version and phase via `scripts/set-version.ps1` (four-part
version only). The packaging scripts do not modify source files and will fail
fast on a version mismatch between `mod/Info.json` and `ADOFAI.Renderist.csproj`.
