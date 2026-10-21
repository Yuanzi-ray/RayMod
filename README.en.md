# RayMod

Languages: [中文](README.md) | English

> ## ⚠️ Read this first
>
> **This repository is the SOURCE-ONLY release of RayMod** (Steam Workshop item: `sharedfiles/filedetails/?id=3820248420`).
> Third-party copyrighted assets used by the mod (**Arknights voice lines, Spine skeletons, character and card art**)
> are **intentionally excluded** — so **cloning this repo will not produce a working mod**.
>
> 👉 **Full details in [`SOURCE-NOTICE.md`](SOURCE-NOTICE.md)** (what is here · what is not · how to supply your own assets).

---

## About this mod

Adds "Ray" (莱伊), the well driller from *Arknights*, to *Slay the Spire 2* as a playable character.

* **92 cards** (7 basic · 20 common · 36 uncommon · 26 rare · 3 ancient) · 32 powers · 4 relics
* **Three signature mechanics**: Depth · Ammo · Sand Beast
* Three switchable skins (default / Summer / Radiant)
* 28 Chinese + Japanese voice lines, combat SFX, card-play and hit VFX

**Requirements**: subscribe to both `STS2-RitsuLib 0.6.7+` and `MinionLib 0.6.3+`; game version `0.111.0+`.

**Author**: Yuanzi_ · Unofficial fan work, **non-commercial use only**.

---

<details>
<summary>The rest of this file is the upstream RitsuLib mod-template documentation (kept for reference)</summary>

A copyable, buildable RitsuLib mod template providing a general Godot/C# project layout, sample content, and static placeholder assets.

**What the template includes:**

- A `[ModInitializer]` entry point plus a minimal custom character (with character card pool, relic pool, and potion pool).
- Four starter strikes, four starter defends, and one starter relic as samples.
- Minimal static Godot placeholder scenes for the combat character, energy counter, character select background, merchant, and rest site.
- Placeholder PNG files copied from vanilla resources and renamed for the template. Replace them after copying.
- Basic English and Simplified Chinese localization files.
- A complete Godot project, export preset, mod manifest, and MSBuild scripts.

## Learning Resources

- [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib): the shared framework library for Slay the Spire 2 mods. This template uses it for content registration, character scaffolding, and Godot resource integration.
- [RitsuLib Documentation](https://github.com/GlitchedReme/SlayTheSpire2ModdingTutorials/tree/master/RitsuLib): tutorials and examples by file.
- [Slay the Spire 2 Modding Tutorials site](https://glitchedreme.github.io/SlayTheSpire2ModdingTutorials/index.html): the full tutorial site.
- Template Wiki (Rider-first): [Chinese Home](https://github.com/alkaid616/RayMod/wiki/Home) | [English Home](https://github.com/alkaid616/RayMod/wiki/Home-EN).

## Install and Use

You can get this project two ways: via the NuGet template (automatic rename), or by copying the directory manually.

### Option A: NuGet template (recommended)

```powershell
# Install the template
dotnet new install STS2.RitsuLib.ModTemplate

# Create a new mod
dotnet new ritsulibmod -n MyMod

# Uninstall the template
dotnet new uninstall STS2.RitsuLib.ModTemplate
```

`dotnet new ritsulibmod -n MyMod` generates a project called `MyMod` and renames `RayMod`, sample class names, sample resource file names, resource folders, manifest names, namespaces, and localization IDs to match the new name.

### Option B: manual copy

1. Copy the whole directory and rename it to your mod name.
2. Edit `RayMod.json` and update `id`, `name`, `author`, and `description`.
3. Edit `RayModCode/Entry.cs` and update `ModId`.
4. For a full rename, also update the project name and namespace in `.csproj`, `.sln`, and `project.godot`.
5. Rename the resource directory `RayMod/` to your `ModId`, then update the related `Entry.ResPath` paths in code.

## Local Path Configuration

```powershell
Copy-Item .\local.props.template .\local.props
```

Set these values in `local.props` (the file is in `.gitignore`; do not commit it):

| Field | Description |
|---|---|
| `Sts2Dir` | Slay the Spire 2 install directory |
| `Sts2DataDir` | Game DLL directory, usually `$(Sts2Dir)/data_sts2_windows_x86_64` |
| `GodotExe` | MegaDot/Godot executable used to export the PCK |
| `RitsuLibDeployDir` | Local RitsuLib deployment directory, defaulting to `$(Sts2Dir)/mods/STS2-RitsuLib`. Used by RitsuLib package/build logic to copy RitsuLib into the game's mods directory — **not** this mod's output directory |

## RitsuLib Version Compatibility

> ⚠️ **Important: align the manifest's RitsuLib version with the csproj before release**
>
> `dependencies[STS2-RitsuLib].version` in `RayMod.json` **must exactly match** the `STS2.RitsuLib` version your `.csproj` actually compiles against. The template build auto-syncs this dependency version; `min_game_version` and intentional lower runtime-floor declarations still need manual review. See [Pre-release checklist: version alignment](#pre-release-checklist-version-alignment) below for the step-by-step procedure.

### Current version snapshot (as of 2026-10-21)

| Item | Value |
|---|---|
| Current STS2 game version | `0.111.0` |
| Current RitsuLib version | `0.6.7` |
| Build target of this project | `RitsuLibTargetVersion = 0.111.0` (mainline `STS2.RitsuLib` package) |
| Template manifest status | `min_game_version` (`0.111.0`) and `dependencies[STS2-RitsuLib].version` (`0.6.7`) are aligned |

### Version mapping

The table summarizes the mainline STS2 target for each boundary RitsuLib release, sourced from the [STS2-RitsuLib Releases](https://github.com/BAKAOLC/STS2-RitsuLib/releases) page. Patch versions not listed follow the range they sit in; check the relevant release notes for boundary versions.

| RitsuLib version | Mainline STS2 target | Compat packages |
|---|---|---|
| `v0.6.7` (since 2026-10-15, **used by this project**) | `0.111.0` | none (use the mainline `0.6.7` package) |
| `v0.3.0+` (since 2026-05-22) | `0.106.0` | `0.103.2`; `0.104.0` compat removed |
| `v0.2.29` ~ `v0.2.40` | `0.105.1` | `0.104.0`, `0.103.2` |
| `v0.2.27` ~ `v0.2.28` | `0.105.0` | `0.104.0`, `0.103.2` |
| `v0.2.0` ~ `v0.2.26` | `0.104.0` | `0.103.2` (experimental from `v0.2.6`); `0.99.1` compat removed in this range |
| `v0.0.x` / `v0.1.x` | `0.99.1` and earlier | — |

### Package selection: mainline and compat

The template references mainline `STS2.RitsuLib` by default, tracking the latest NuGet version:

```xml
<PackageReference Include="STS2.RitsuLib" Version="*" GeneratePathProperty="true" />
```

**Only enable one RitsuLib package at a time.** If your code still targets an older branch, comment out the mainline and enable the matching compat package:

```xml
<!-- STS2 0.104.0 compatibility branch (no longer maintained since v0.3.0) -->
<PackageReference Include="STS2.RitsuLib.Compat.0.104.0" Version="*" />

<!-- STS2 0.103.2 compatibility branch -->
<PackageReference Include="STS2.RitsuLib.Compat.0.103.2" Version="*" />
```

Compatibility packages only select the matching game branch; they do not restore every old API. Some old mods still need code changes and recompilation.

The template also references `Nothing.STS2RitsuLib.ModAnalyzers` — an AI-written helper analyzer that reports common manifest and resource configuration issues during development.

### Pre-release checklist: version alignment

> **`PackageReference` in `.csproj` only controls compile-time resolution; `dependencies` in `RayMod.json` is what the game loader checks at runtime. The template syncs the `STS2-RitsuLib` dependency version to the resolved NuGet version during build, but `min_game_version` still needs manual review.**

If the manifest is not synced to the newer RitsuLib version you compile against, players with an old RitsuLib will pass the manifest check and crash at runtime due to missing APIs or signature drift. Conversely, an over-tight manifest will reject players who could otherwise run the mod.

Before every release:

1. After build, confirm `dependencies[STS2-RitsuLib].version` in `RayMod.json` has been synced to the resolved `STS2.RitsuLib` version.
2. When you switch to a compatibility package (`Compat.0.104.0` / `Compat.0.103.2`), also adjust `min_game_version` to the matching branch. Keep `dependencies[].id` as `STS2-RitsuLib` (compatibility packages expose the same mod id to the loader).
3. If you intentionally want the manifest version to act as a **runtime floor** (e.g. declaring "`0.3.0+` works"), document this in your release notes and verify the mod runs against the declared floor.

### Upgrade notes

#### Upgrading to RitsuLib `v0.3.0` / STS2 `0.106.0`

Major changes (from the [v0.3.0 release notes](https://github.com/BAKAOLC/STS2-RitsuLib/releases/tag/v0.3.0)):

- **Breaking**: `RunSidecar` removed, fully replaced by `RunSavedData`.
- New `TargetType` registration capability for custom `TargetType`s.
- Loader target detection strengthened: branch version files now use hash verification, and mismatched versions are discarded.
- `0.104.0` compatibility removed.

#### Upgrading to RitsuLib `v0.2.27` / STS2 `0.105.0` (historical)

When migrating from an earlier branch (`v0.2.0` ~ `v0.2.26` / STS2 `0.104.0`), check the following:

- Version conditional compilation switched to cumulative interval macros `STS2_AT_LEAST_<ver>`; legacy `STS2_V_<ver>` macros are no longer recommended.
- AnyPlayer / AnyAny targeting logic changed; legacy card targets, base constructor signatures, and registration flows should be checked against the new API.
- Cards support extra icon count labels in the lower-right corner with vanilla UI conflict handling; verify display order and placement for custom UI / icon patches.
- Retain / flush hooks and events have replacements, removals, or `[Obsolete]` markers; migrate legacy uses of `CardRetainedEvent`, `CardsFlushedEvent`, or legacy `Hook.*` entry points.
- `Badge`, `BadgeRuntimeTemplate`, `BadgePool.CreateAll`, and `ModBadgeTemplate` constructor signatures changed; legacy code may need updates to avoid `MissingMethodException`.

## Build

| Command | Behavior |
|---|---|
| `dotnet build .\RayMod.csproj` | Full build: compile + `CopyMod` + `ExportPCK` |
| `... /p:RunPckExport=false` | Skip PCK export (no `GodotExe` needed) |
| `... /p:CopyModOnBuild=false` | Skip copying to the game's mods directory (output stays in `bin/`) |
| `... /p:RunPckExport=false /p:CopyModOnBuild=false` | C# compile validation only |

A full build runs two MSBuild targets after `Build`:

- **`CopyMod`**: copies the DLL and manifest to the game's `mods/RayMod` directory.
- **`ExportPCK`**: calls `GodotExe` and exports the PCK to the same mod directory.

> `RitsuLibDeployDir` only controls where the RitsuLib framework itself is deployed locally. This mod's DLL, manifest, and PCK are controlled by `ModOutputDir` (default `$(Sts2Dir)/mods/$(MSBuildProjectName)`).

## Directory Layout

```text
RayMod/
├── RayModCode/   # C# source
├── RayMod/       # Godot resources, localization, and placeholder scenes
├── RayMod.csproj
├── RayMod.json   # Mod manifest
├── project.godot
└── local.props.template
```

`res://RayMod/...` is the Godot/PCK resource path, mapping to the repository resource directory `RayMod/`, **not** to the C# namespace. When you create a project from the NuGet template, these directory names, file names, and namespaces are renamed consistently to match the new mod name.

## Template Contents

### Sample character

| Property | Value |
|---|---|
| Type | `RayModCharacter` |
| Expected ID | `RAY_MOD_CHARACTER_RAY_MOD_CHARACTER` |
| Starter deck | 4 × `RayModStrike`, 4 × `RayModDefend`, 1 × `RayModRelic` |
| Assets | Configured via `CharacterAssetProfile`. The template only specifies static placeholder assets; unspecified audio, trail, transition, etc. fall back through `PlaceholderCharacterId` |

### Sample cards and relic

| Type | Pool | Expected ID |
|---|---|---|
| `RayModStrike` (attack) | character card pool | `RAY_MOD_CARD_RAY_MOD_STRIKE` |
| `RayModDefend` (skill) | character card pool | `RAY_MOD_CARD_RAY_MOD_DEFEND` |
| `RayModRelic` | `RayModRelicPool` | `RAY_MOD_RELIC_RAY_MOD_RELIC` |

### Static placeholder assets

**Images** (`res://RayMod/images/...`):

- `cards/RayModStrike.png`, `cards/RayModDefend.png`: sample card art.
- `relics/RayModRelic.png`: sample relic icon.
- `characters/RayMod_character_*.png`: character icons, select art, map marker, and energy icons.

**Scenes** (`res://RayMod/scenes/characters/...`):

| Scene | Purpose | Placeholder structure |
|---|---|---|
| `RayMod_character.tscn` | Combat character | `%Visuals`, `%Bounds`, `%IntentPos`, `%CenterPos`, `%TalkPos` |
| `RayMod_energy_counter.tscn` | Energy counter | `%EnergyVfxBack`, `%Layers`, `%RotationLayers`, `%EnergyVfxFront`, `Label` |
| `RayMod_merchant.tscn` | Merchant | — |
| `RayMod_rest_site.tscn` | Rest site | `%ControlRoot`, `%SelectionReticle`, `%Hitbox`, `%ThoughtBubbleRight`, `%ThoughtBubbleLeft` |
| `RayMod_character_select_bg.tscn` | Character select background | — |

These resources only exist to make the template visible and replaceable; they do not try to reproduce vanilla animation quality. After copying the template, replace them with your own assets. If you change paths, update the corresponding `AssetProfile` fields.

## Manifest Format

`RayMod.json` is the mod manifest. The game loader reads it at startup to identify the mod, check dependencies, and decide whether to load. Full example:

```json
{
  "id": "RayMod",
  "name": "RayMod",
  "pck_name": "RayMod",
  "author": "Author",
  "description": "A starter Slay the Spire 2 mod template built on RitsuLib.",
  "version": "0.0.0",
  "has_pck": true,
  "has_dll": true,
  "affects_gameplay": true,
  "min_game_version": "0.111.0",
  "dependencies": [
    { "id": "STS2-RitsuLib", "version": "0.6.7" }
  ]
}
```

### Field reference

| Field | Type | Description |
|---|---|---|
| `id` | string | Unique mod identifier. **Must match `Entry.ModId` exactly**, and should also match the `mods/<id>` directory name. In-game dependency lookups, localization key prefixes, and resource paths all depend on this value |
| `name` | string | Display name shown in the mod list. May contain spaces and non-ASCII characters |
| `pck_name` | string | `.pck` file name (without extension). **Must match the actual PCK file produced by `.csproj`**, or resources will not load even with `has_pck=true` |
| `author` | string | Display-only author name |
| `description` | string | Short description shown in the mod list |
| `version` | string | Version of this mod itself. SemVer (`MAJOR.MINOR.PATCH`) is recommended. Bump on every release |
| `has_pck` | bool | Whether the mod ships a `.pck`. Code-only mods can set `false` and skip `ExportPCK` |
| `has_dll` | bool | Whether the mod ships a `.dll`. Resource-only mods can set `false` |
| `affects_gameplay` | bool | Whether the mod affects gameplay. When enabled, the game flags saves/achievements/etc.; only purely cosmetic / localization mods should set this to `false` |
| `min_game_version` | string | Minimum compatible STS2 version. Older games refuse to load. **Must align with the game branch targeted by the RitsuLib package selected in `.csproj`** (see [RitsuLib Version Compatibility](#ritsulib-version-compatibility) above) |
| `dependencies` | array | Dependency list. Each entry uses `id` + `version`. **The legacy single-object `min_version` form is no longer supported** |
| `dependencies[].id` | string | The depended-on mod's `id`. RitsuLib itself uses `STS2-RitsuLib` |
| `dependencies[].version` | string | Minimum runtime version of the dependency. **The `STS2-RitsuLib` value must exactly match the NuGet version your `.csproj` actually compiles against** — see [Pre-release checklist: version alignment](#pre-release-checklist-version-alignment) above |

## Development Tips

- Prefer `AssetProfile` for new content; only override legacy `Custom...Path` fields for individual compatibility cases.
- If a character resource field is not specified, RitsuLib fills it from the vanilla character config referenced by `PlaceholderCharacterId`.
- Resource paths must start with `res://`; verify the directory name and casing inside the PCK are correct.
- For `.tscn` files, make sure the scene is packed into the mod resources. If it needs a script, prefer a local wrapper class and call `EnsureGodotScriptsRegistered(...)` from `Entry.Initialize()`.

</details>
