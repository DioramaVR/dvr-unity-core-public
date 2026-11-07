# dvr-unity-core

The shared VR code behind the [Diorama VR](https://dioramavr.app) mods: tabletop
and diorama VR for Unity games, through SteamVR (OpenVR). This repo is a
read-only copy, published from each tagged release. The version is in `VERSION`,
and `CHANGELOG.md` lists what changed.

## How the mods use it

Each mod compiles this core into its own DLL as source. That way one copy of the
code builds for both BepInEx runtimes:

- BepInEx 5 / Mono (net472);
- BepInEx 6 / IL2CPP (net6.0);
- no BepInEx (netstandard2.1, Unity 6.5).

The differences are handled with `#if` on `DVR_MONO` / `DVR_IL2CPP`, and on
`DVR_BUILTIN` / `DVR_URP` for the render pipeline.

In the mod's csproj, set the runtime and the pipeline, then import the props:

```xml
<DvrRuntime>Mono</DvrRuntime>        <!-- or IL2CPP -->
<DvrPipeline>BuiltIn</DvrPipeline>   <!-- or URP -->
<DvrCoreDir>path\to\dvr-unity-core\</DvrCoreDir>
<DvrCoreVersion>0.1.1</DvrCoreVersion>
...
<Import Project="$(DvrCoreDir)DvrUnityCore.props" />
```

The props file adds the core's `.cs` files as linked `Compile` items and defines
the runtime and pipeline symbols. It adds no references: the mod keeps its own
(BepInEx, the UnityEngine modules, TextMeshPro, and the URP runtime for URP games).
The build stops if `DvrCoreVersion` isn't the version of the core it finds.
Release builds carry no PDB.

In the plugin's Awake/Load, first thing:

```csharp
VrCore.Init(Logger, "MyGameVr");   // the log, and the prefix for the core's object names
```

Every hook a mod plugs into is described in [docs/HOOKS.md](docs/HOOKS.md). The
menu's drawing kit is in [docs/MENU-KIT.md](docs/MENU-KIT.md).

## What's in it

| Folder | What |
|---|---|
| `src/Core` | `VrSession` (OpenVR, poses, eye projection, frame submit), `VrHand` (controller input, haptics), `Pose` |
| `src/OpenVR` | Valve's `openvr_api.cs` |
| `src/Rig` | the table rig: a world on a table you move, scale and turn with your hands |
| `src/UiKit` | the VR menu, the game's UI on panels and windows, rebindable actions, button bars |
| `src/Capture` | photos, video (through ffmpeg) and a smoothed spectator view |
| `src/Diorama` | the cylinder cut for Turntable mode |
| `src/Pipeline` | eye and spectator camera settings for the built-in pipeline and URP |
| `fonts/`, `icons/` | the menu's fonts and Lucide icons; a mod ships them next to its DLL |

## Licence

MIT, Copyright (c) 2026 scrire (`LICENSE`). The OpenVR bindings, the fonts and the
icons keep their own licences, listed in `LICENSE` and next to the files.
