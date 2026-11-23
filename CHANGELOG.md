# dvr-unity-core changelog

Versions follow `VERSION` and are tagged `vX.Y.Z`. While the core is 0.x:
- **minor** (0.X.0): a change a mod has to act on (a removed or changed API, a new required reference);
- **patch** (0.0.X): fixes and additions a mod can ignore.

Each mod says which version it's built and tested with (`DvrCoreVersion` in its
`Directory.Build.props`); the build stops if the core checkout is another
version, and the installer builds each mod against the core tag it names. See
README.md, "Versions".

## 0.1.3 (release branch `release/0.1.3`, not tagged yet)

Nothing a mod has to change in code; a rebuild picks everything up. A mod
with its own eye cameras can use `Recorder.WithoutOurs` for clean photos.
Diorama capture is off unless a game offers it.

- **Fix: our VR layer is never left out of a shot when the game uses it**
  (`OurVisuals`). Photos, the spectator and the diorama capture leave our VR
  layer out only when it's ours alone (not 0, not a layer the game named).
  Cataclismo falls back to Default (0); leaving that out would have taken the
  game's world with it. Then our renderers (roots named after the core, or
  `VrUi ...`) are switched off for the shot instead. Popout layers are always
  left out.

- **Photo and video countdown** (`Recorder`, `CaptureCountdown`). Photo (and
  starting a video) hides the VR menu and button bars at once, counts down
  (`[Recording] CountdownSeconds`, 3; 0 = at once) with dots over the laser
  hand, then shoots; pressing again cancels. The photo's frames leave all of
  ours out of the eyes and the spectator (the VR layer: laser, hands, panels,
  bars, the dots; popped-out windows). Then the menu and bars come back. New
  bindable actions "Take photo" and "Start or stop video" (Controls tab,
  unbound by default), so neither needs the menu. Nothing for a mod to change
  unless it has its own eye cameras (`Recorder.WithoutOurs`).

- **Pacing paused while the headset is gone** (`VrPacing`). When the headset
  goes to standby, or SteamVR's compositor keeps WaitGetPoses blocked (10 waits
  in a row over 3 frames; the Steam Frame's link dropping did this, and the
  flat game crawled at about 9 fps), the game stops waiting and submitting,
  reads poses without blocking and gets its own vsync and frame cap back
  (`VrBoot.UncapFrameRate`). Every 2 s one WaitGetPoses probes; a quick one,
  out of standby, resumes VR pacing. Logged both ways. Nothing for a mod to
  change.

- **Fix: quitting from SteamVR** (`VrQuit`; not yet confirmed in a headset:
  a Cataclismo test still didn't close from the dashboard, being looked into). `VrSession.BeginFrame` now reads
  every SteamVR event each frame. On SteamVR's quit (the dashboard's Quit,
  SteamVR shutting down) it acknowledges, stops all compositor waits and submits
  (main and render thread, so nothing blocks Unity's shutdown) and quits the
  game. Before, no mod read the event, and SteamVR killed the game after its
  timeout. `VrKeyboard` reads the same events (`VrSession.Events`). Nothing for a
  mod to change; a rebuild picks it up.

- **Diorama capture** (`DioramaCapture`, bead vr-843): "Capture diorama" on the
  Record tab, for a game that calls `DioramaCapture.Offer`. Captures the table's
  region as 208 views with a nerfstudio `transforms.json`, for a Gaussian splat
  and a game page's turntable (docs/DIORAMA-CAPTURE.md). Moved from TpmDiorama.
  Off unless offered; hooks for freezing, hiding in-world UI and framing.

## 0.1.2 (tagged `v0.1.2`, 2026-11-23)

Nothing a mod has to change in code; every mod needs a rebuild to get the fixes.
For the Steam Frame, the game's Steam app data also needs a VR launch option
running the exe with `-dvr-vr` (the installer writes it; docs/STEAM-FRAME.md).
Also: `VrSession.LogPoseDiagnostics` (off) logs head-pose validity for a minute;
the start-up log now has each eye's projection tangents and eye-to-head.

- **Fix: the eye projection's vertical was mirrored** (`VrSession.EyeTangents`,
  `TangentsFromRaw`). It now builds the frustum Valve's ComposeProjection does
  from GetProjectionRaw (pfTop is the slope at the bottom of the view). On
  headsets with equal up and down angles (Index) nothing changes. On the Steam
  Frame (49 deg up, 60 deg down) the picture stretched looking down and swam
  with the head. Culling (`RigMath.FitFrustum`, CylinderCull's far fit) uses
  the same tangents and is fixed with it. Mods only get it once rebuilt.

- **Launch options `-dvr-vr` and `-dvr-flat`** (`VrStart`, docs/HOOKS.md
  section 1). `-dvr-vr` (a VR launch option; the Steam Frame uses it) asks
  Steam to start SteamVR on a background thread (`SteamVrStarter`), waits for
  the headset, then starts VR, once; if that fails, it isn't tried again that
  session (a failed `OpenVR.Init` freezes the game for about 15 s). SteamVR
  already running now needs a headset (vrcompositor) before Init. `-dvr-flat`
  keeps the game flat. A game launched from the Steam Frame's desktop stream (`SteamStreamingVRTransitionInvite`) stays
  flat and says so in its log and, for 10 s, on the flat screen (`FrameNotice`:
  the player is sent to the installer's Fix Steam Frame launch): starting
  SteamVR from the game doesn't bring the Frame into VR (docs/STEAM-FRAME.md). A desktop launch with SteamVR off
  still plays flat. Nothing for a mod to change.

- **`DvrPipeline` Custom**, for a game on its own scriptable render pipeline
  (roguetrader-vr: Owlcat's Waaagh). The core compiles no pipeline file and
  defines `DVR_CUSTOM_PIPELINE`; the mod brings `Dvr.VrPipeline`, starting from
  `src/Pipeline/Custom/VrPipeline.Custom.cs.template` (README, "Using it").
  `check/Mono.Custom` compiles the template. BuiltIn and URP are unchanged.

## 0.1.1 (tagged `v0.1.1`, 2026-11-07; headset-tested on Two Point Museum)

Nothing a mod has to change. Every addition below is optional.

- **One Arranged mode.** Free is merged into Arranged: windows sit round the
  table and can be moved while the VR menu is open. `VrUi.Offer` and `SetMode`
  treat Free (and Separate) as Arranged. Saved settings carry over once
  (`[VrUi] OneArranged`): Free and Separate become Arranged, and an old
  Arranged layout is kept as `free.before-arranged.txt`. Sims that grab a
  window must open the VR menu first.
- **Pink outline** (`UiOutline`) on everything movable while the menu is open:
  windows, button bars and the pane. Brighter on hover, solid while held,
  yellow (the theme's `#f7b801`) where letting go snaps it to the wrist.
- **A pin per window** (`UiWindowDef.Pin`, `UiPin`): the point that stays put
  when the window changes size. Nine dots while arranging re-pin it. Saved in
  the layout only when set.
- **Button bars**
  - Carry and snap to the wrist like windows, and turn with the hand.
  - Icons (`icon:`, `.Icon()`; 77 Lucide icons) and the game's own art
    (`art:`, `.Art(Func<Sprite>)`), asked for 4 times a second so a slot
    whose skill changes swaps its picture. Live labels (`.LiveLabel`).
  - Buttons tied to the game's actions (`.Action(label, actionId, fallbackKey)`,
    `VrHotkeys.UseActions`), so they keep working after a remap.
- **Help**
  - The game's own questions come first, under its name, then its extra
    tips, then the core's under "Every game" (`FaqItem.Heading`).
  - Help text names controls as they're bound now: `{bind:id}`,
    `{press:id}`, `{bind?:...}`, `{laser hand}`, `{other side}` and the
    others (docs/HOOKS.md). Unbound actions read "(not bound: see Controls)".
- **Graphics:** Shadows and Post-processing show only where they work (URP),
  or where the mod handles them (`RigSettings.HandledByMod(field)`).
- **Home:** the button bars' subtitle no longer overlaps the tiles.
- **Release builds have no PDB** (`DvrUnityCore.props`: `DebugType none` when
  `Configuration` is Release). A PDB puts its full path, including the build
  machine's home folder, into the DLL. Debug builds are unchanged. A mod that
  set this itself can drop it.
- docs/BRANCHING.md: main, development, feature/*, release/*.
- **LICENSE:** the core is MIT, Copyright (c) 2026 scrire. The OpenVR bindings,
  fonts and icons keep their own licences (listed in LICENSE).

## 0.1.0 (tagged `v0.1.0`)

The first versioned core. Everything up to 2026-11-06, notably:

- **Needs `UnityEngine.TextRenderingModule`** referenced by the mod (VrFont uses `Font` / `TextAnchor`).
- **UI modes and windows:** Panel / Arranged / Free, and for Arranged and Free,
  Popout (each window drawn on its own, `VrPopout`) or Cutout.
  - `VrUi.Offer`, `VrUi.OfferPopout`, `UiWindowDef.Cutout`, `OpensFrom` / `OpensSide`.
  - Popout widens a URP renderer's layer filter once, at load.
- **World UI:** `VrWorldUi` (a whole canvas of world-anchored UI drawn in the
  world, clickable) and `VrWorldCanvas` (single-canvas signs).
- **Carrying:** the holding hand's own physical stick moves and resizes what it
  holds. Hotkey bars are one per set, each switched on by itself. Everything
  but the VR menu can go on a wrist, the game UI pane included. Wrist windows
  are always shown (`WristButton` does nothing).
- **The menu draws in any game:** `VrFont` (safe TMP font lookup, UI Text
  fallback where TMP can't run), and stand-in materials where `UI/Default` is
  missing. Window names (`VrLabels`).
- **New shared pieces:**
  - `VrKeyboard` (SteamVR keyboard for text fields);
  - `VrBoot` (start-up chores);
  - `VrRecon` (diagnostic dump);
  - `VrCinema` and `VrVisuals.Screen`;
  - the headset-free test kit (vr-s4q.4): `SimRunner`, `SimHands`, `SimShots`,
    `TexturePng` (the Recorder's photo uses it), and `tools/sim-common.sh` /
    `wait-and-run.sh` with one list of all DVR games and a verified save
    snapshot and restore. HOOKS.md 7b covers how to move a mod's sim over.
- **The standard table rig** (vr-urz.2, HOOKS.md 7c): `TableRig` with
  Diorama, Turntable and Orbit; the hosts `PivotHost` / `CharacterHost`;
  `TableModel`, `RigMath`, `RigSettings` (`[Dvr.*]` sections, old keys moved
  once); `TableActions.RegisterPan`. Additive: `VrRig` is unchanged.
  - Then, from the games moving over: Diorama's sticks are TPM's (off stick
    turns and zooms, pointer stick pans) as default bindings,
    `TableActions.RegisterTableModes`, and the rig no longer sets
    `VrInput.SwapSticks`. `Register(context, stickBindings: false)`.
  - Host hooks: `Taken`, `Sticks` (echo or drive), `GameZoomDistance`,
    `FollowGroundHeight`. `OldKey.Value`, `rig.OldMode`, `rig.SaveScale`,
    `HandMarkers`, the shadow-distance cap (`VrPipeline.CapShadowDistance`).
  - Background per mode (with `Game`), an Edge row for Orbit's cut, and late
    registration marks the menu for a rebuild.
- **The table rig, step 1** (vr-urz.1; design in docs/RIG.md): `VrDesktop`
  (desktop blanking, as the five pivot rigs each did it) and the eye helpers
  `VrPipeline.SetPostProcessing`, `CopyRenderer` and `RendererIndex`.
  `CopyEyeSettings` now copies the game's URP renderer on IL2CPP too (no
  IL2CPP mod called it before). Optional: nothing changes until a mod switches.
- **Heading font:** Tilt Warp for the menu's title, tabs and section headings (`VrFont.UseModFolder`, `HeadingFontFile`, `HeadingLegacyFont`, `HeadingFont`); `fonts/TiltWarp.ttf`. The Welcome and UI tabs now say how to move the panel, the bars and windows.
- **Fixes:** `CylinderCull.FitFar` keeps our own UI inside the fitted far plane.
- **Versioning:** `VERSION`, `VrCore.Version` (logged at start-up), and the
  `DvrCoreVersion` check.
