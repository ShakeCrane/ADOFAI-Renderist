# Local Reference Setup

ADOFAI Renderist uses developer-local ADOFAI / Unity / Unity Mod Manager assemblies for building. No proprietary DLL is committed to this repository.

## Current supported baseline

- ADOFAI Steam App ID: `977950`
- Supported public Steam buildid: `24397494`
- `Assembly-CSharp.dll` FileVersion: `0.4.3.0`
- Unity: `6000.3.10f1`
- Runtime: Mono / Managed (`MonoBleedingEdge/` present, no `GameAssembly.dll` baseline)
- Unity Mod Manager: `0.33.0`
- Harmony: UMM-bundled `0Harmony.dll` FileVersion `2.3.6.0`
- Target framework: `net48`

The project supports only this latest validated ADOFAI public Steam baseline. `scripts/prepare-references.ps1` requires the expected `Assembly-CSharp.dll` FileVersion and, when the Steam appmanifest is available, also requires the exact Steam buildid. If the appmanifest cannot be found, the script warns that Steam build identity could not be established even though the Assembly-CSharp baseline matched; runtime validation remains required.

Do not infer the supported game build from a human-facing version label alone. The machine gate is the current public Steam buildid together with the validated `Assembly-CSharp.dll` FileVersion.

`Assembly-CSharp.dll` is used only as a local runtime-analysis/reflection baseline. It is **not** a compile-time project reference and must never be committed.

## Local paths

Compile-time references are read directly from the developer's own ADOFAI / UMM installation. The repository does not track a `references/` tree; `build/local.props` is generated locally by the script below and is gitignored.

Typical Steam install:

```text
C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice
```

Run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-references.ps1
```

Or specify paths explicitly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-references.ps1 `
  -AdofaiDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice" `
  -UmmDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\UnityModManager"
```

The script resolves the install, validates the supported ADOFAI baseline, checks Mono/Managed state and required build DLLs, then writes `build/local.props`.

## Required compile-time DLLs

The C# project references only:

- `UnityModManager.dll`
- `0Harmony.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.ImageConversionModule.dll`
- `UnityEngine.dll` only when that legacy umbrella assembly exists

ADOFAI internal APIs are resolved at runtime by `EditorGameReflection` / `RenderistAutoPlay`; `Assembly-CSharp.dll` is deliberately absent from the `.csproj`.

## Build

```powershell
dotnet build .\src\ADOFAI.Renderist\ADOFAI.Renderist.csproj -c Debug
dotnet build .\src\ADOFAI.Renderist\ADOFAI.Renderist.csproj -c Release
```

## Release packaging

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Force
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-package.ps1 `
  -ZipPath .\dist\ADOFAI.Renderist.zip
```

The release zip is always:

```text
dist/ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

`package-release.ps1` cross-checks `Info.json`, the project `<Version>`, and `ModEntry.ModVersion` before building/packaging. `verify-release-package.ps1` checks package contents and the built DLL version.

## Hygiene

- Never commit `build/local.props`.
- Never commit ADOFAI, Unity, UMM, Harmony or third-party Mod DLLs.
- Never commit decompiled game source.
- `dist/`, `bin/`, `obj/` and local runtime caches stay untracked.
- Re-run `prepare-references.ps1` after an ADOFAI / Unity / UMM update; unsupported ADOFAI baselines must not be treated as compatible without a new project validation cycle.

To remove the local-only build configuration (`build/local.props`):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\clean-local-config.ps1
```

Use `-WhatIf` to preview without deleting. `scripts/prepare-references.ps1` regenerates the file on the next run.
