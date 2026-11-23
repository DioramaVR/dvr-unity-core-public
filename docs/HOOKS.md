# dvr-unity-core: hook reference

Everything a Unity mod plugs into, in one place. What the mod must build
(which surface does what, the controls) is in the engine-neutral
[STANDARDS.md](../../../docs/STANDARDS.md). The menu design is in
[MENU.md](MENU.md), and how to set up the build in the [README](../README.md).

Conventions:
- **Tracking space** is the play space in metres (OpenVR's, in Unity's axes).
  **World** is the game world the eyes draw.
- **Settings** go to the game's config file, in the core's sections (`[VrUi]`…).
- **"Used by"** names the mods that use a hook today, as examples to copy. When
  this page and a mod disagree, the code here is right.

## 1. Start-up, in the plugin's Load / Awake

```csharp
VrCore.Init(Logger, "AtsVr");                // first: logging and the prefix for GameObject names
VrCore.VisualLayer = 31;                     // only if the game's cameras mask out 31 (cataclismo: 0)
var vr = VrSession.TryStart(pluginDir);      // null if VR shouldn't start (below); VrSession.StartSimulated() for sims
VrUi.Init(config, "atsvr-layouts");          // UI kit: binds [VrUi], loads layouts; also inits VrModes, VrHotkeys, VrPane, VrPieces
// register windows / pieces / layouts / settings / hotkey sets here (sections 4 to 7)
VrInput.Init(config, "atsvr-controls");
TableActions.Register("Playing");            // the table's actions, if the mod uses the core's VrRig
VrInput.Register(id, name, section, group, "Pointer.Trigger", ...);
VrInput.Load();                              // after every Register
VrMenu.Welcome("Game name", Plugin.Version, "tip 1", "tip 2", "tip 3");
```

### When VR starts (`VrStart`, 2026-11-22)

`TryStart` goes into VR by these rules, first match wins, and logs which one
fired (`[vr] ...`, once per change; mods retry every few seconds while flat):

1. **`-dvr-flat`** on the game's command line: flat, even with SteamVR running.
2. **The core's own start of VR failed** earlier this session: flat, no more
   tries. A failed `OpenVR.Init` freezes the game for about 15 s, so it is
   tried once only.
3. **`-dvr-vr`** on the command line: a VR launch option. This is how the
   Steam Frame starts a game in VR, once the game's Steam data has a launch
   option of type `vr` that passes `-dvr-vr` (docs/STEAM-FRAME.md). The mod can
   also set `VrSession.LaunchSteamVr`. `SteamVrStarter`, on a background
   thread, asks Steam to start SteamVR (`steam://rungameid/250820`) and waits
   up to 2 minutes for `vrcompositor` (SteamVR has the headset). The game plays
   flat meanwhile, then `OpenVR.Init` runs once. Nothing on Unity's main thread
   waits for SteamVR.
4. **SteamVR is running with a headset** (vrserver and vrcompositor): VR. This
   is how most players start it. vrserver alone (no headset yet) waits, since
   Init would block until a headset joins.
5. **Launched from the Steam Frame's flat stream** (Steam sets
   `SteamStreamingVRTransitionInvite`, no `-dvr-vr`): flat, with a hint in the
   log and, for 10 s, a message on the flat screen (`FrameNotice`: "Steam Frame:
   VR isn't set up for this game. Open the DioramaVR installer and press Fix
   Steam Frame launch."; a desktop overlay, never in VR). Starting
   SteamVR from a flat stream doesn't bring the Frame in, and froze games on
   2026-11-22.
6. Otherwise flat: a desktop launch with SteamVR off.

## 2. Every frame, in this order

| Call | What it does |
|---|---|
| `vr.BeginFrame()` | Reads the poses. |
| `VrInput.Update(host, vr.Left.State, vr.Right.State)` | Updates actions, the chord and carrying the panels (menu, bar, pane). |
| `VrUi.HandGrab(offHand, pressed, held)` | Hand grabs of windows (Arranged, menu open). (`VrUi.WristButton` does nothing now: wrist windows are always shown, vr-4j0.12. Drop it and its binding.) |
| `rig.Update()` | The table (or the mod's own rig). |
| `screenUiPanel.Update(rig, rig.Active)` | The game UI pane. |
| `VrUi.Update(host)` | Menu, hotkey bar, windows and pieces. |
| Laser: `VrUi.Raycast(origin, dir)`, then `VrUi.Click()` on a `Menu` hit, then `VrPieces.LaserEnd(tip)` | Clicks on our surfaces, and where Mouse pieces go. |
| Arranged, menu open: `VrUi.BeginGrab`, `UpdateGrab`, `EndGrab` | Moving windows with the laser. |
| `SpectatorCam.Update(...)`, `Recorder.Update()` | If used. |
| `vr.Submit(left, right)` | Hands the eye images to SteamVR. |

Cataclismo is the reference for this order: `CataUi.cs` and `VrPointer.cs`.

## 3. Interfaces the mod implements

### `IVrUiHost` (UI kit, `src/UiKit/IVrUiHost.cs`)

| Member | Meaning |
|---|---|
| `Active` | VR is running and our UI should draw. False hides the menu (the log says so). |
| `CanArrange` | Somewhere split windows make sense (in a level). False means Panel only. |
| `UiCamera`, `UiTexture`, `UiCanvases` | From `ScreenUiPanel` (`.UiCamera`, `.Texture`, `.Canvases`). |
| `Head`, `LeftHand`, `RightHand`, `LeftHanded`, `TableCentre` | Tracking space. |
| `TrackingToWorld(pose)`, `WorldScale` | From the rig. |
| `VisualLayer` | A layer the eye cameras draw. Our quads go on it. |
| `MakeTexturedMaterial(tex)` | An unlit, see-through material that draws on top (`VrVisuals.Overlay` does this). |
| `PrepareCamera(cam)` | Called for every camera we own, every frame (tpm: keep it out of the game's GI). |
| `ShowFullPanel(show)` | Show or hide the game UI pane (false in Arranged). |
| `ConfigDirectory`, `Log(msg)` | |

### `IDioramaHost` (the rig, `src/Rig/IDioramaHost.cs`)

| Member | Meaning |
|---|---|
| `GameCamera` | Render settings are copied from it. Null means nothing to show. |
| `TryGetFocus(out focus, out grounded)` | What the table centres on. |
| `GripsBusy` | The game's UI wants the grips. |

`RigConfig` holds the rig's settings as the mod's own config entries, and its
limits: `TwistGain` and `OneGripOrbits` (re2-vr's orbit).

### `ITrackingSpace`

Only `TrackingToWorld` and `WorldScale`. `VrRig` implements it. A mod with its
own rig (tpm-vr) implements it to use `SpectatorCam` and `ScreenUiPanel`.

## 4. The game's UI on a pane: `ScreenUiPanel`

`new ScreenUiPanel(widthEntry, heightEntry, distanceEntry)` moves the game's
screen-space canvases onto a hidden UI camera and texture, and shows them on a
floating pane. Hooks:

| Hook | For |
|---|---|
| `IsGameUiCamera(cam)` | Which camera is the game's UI camera (default: `Camera.main`). |
| `PrepareUiCamera(cam)` | Tweaks the hidden UI camera needs. |
| `Placement()` | Where the pane goes this frame (tracking pose and width). Null means the default: in front of the standing player at the Width / Height / Distance settings, tilted back 10°. |
| `PlaceKey()` | The remembered place the player can carry it to: `"Panel"` by default. **Null keeps the pane fixed** (ats: in Cinema). |
| `UiCameraPosition`, `RefreshEveryFrames`, `ScanInterval` | Where the UI camera sits, how often it redraws, how often it looks for new canvases. |
| `ShowFull`, `Current`, `Visible`, `Texture`, `UiCamera`, `Canvases` | Read back. |

Carrying the pane is `VrPane`.
- **It only works in Panel mode, while the VR menu is open**, with the grip on
  the pane. The pane's place and size are kept in `[VrUi] PanePlace <key>`, and
  UI tab → "Game UI panel: Reset place" puts it back.
- A mod drawing its own pane calls `VrPane.Place(key, defaultPose,
  defaultWidth, heightOverWidth, shown)` each frame and draws where it says (tpm-vr).

Used by: ats, cataclismo, deathsdoor, ror2 and tph use `ScreenUiPanel`; tpm has its own pane.

## 5. Windows and pieces (UI mode Arranged; World and Mouse pieces in Panel too)

All of them are cut from the one UI texture: a window is a rectangle of the
screen UI, so anything else drawn in that rectangle shows in it too. See
"Limits" below.

| Call | For |
|---|---|
| `VrUi.RegisterWindow(new UiWindowDef { Id, Title, Roots, Kind, OwnRect, Remainder, Anchor, Pin })` | A window. `Roots`: the objects it shows (re-found 4 times a second; nulls skipped). `Pin`: the point that stays put when it changes size ("Pins" below). |
| `VrUi.RegisterPiece(id, title, UiPieceKind.Screen / World / Mouse, roots)` | Shorthand for a window with a kind. **For an anchor, use `RegisterWindow` with `Anchor` set**: there is no anchor parameter. |
| `UiWindowDef.Remainder = true` | The window that takes whatever no other window claimed (pop-ups). |
| `UiWindowDef.OwnRect = true` | Measure from the roots' own rects, not every graphic under them (roots whose glows spill out). |
| `UiWindowDef.Anchor = element => worldPoint` | World pieces: the world point each element (each active child of a root) belongs to. **Without it** the point is guessed: the centre of the element's screen rect, sent back through `VrPieces.GameCamera` onto `VrPieces.ScreenToWorld`, or the plane y = `GroundHeight`. The guess puts markers that float above a thing behind it, and misses anything the game camera can't see. |
| `VrUi.Named("Obj", "Prefix*")` | Roots by name, anywhere on the UI canvases. |
| `VrUi.Exclude("Name", "Prefix*")` or `VrUi.Exclude(() => transforms)` | UI that belongs in no window. |
| `VrUi.RegisterLayout(layout)` | A built-in layout. Layouts are `UiPlacement`s per window id: an anchor (Room, Table, Head, OffHand, PointerHand), a position, yaw / pitch / roll, a size and a pivot. Player layouts are files in the layout folder. |
| `VrPieces.GameCamera`, `VrPieces.ScreenToWorld`, `VrPieces.GroundHeight` | For World pieces without an anchor. cataclismo sets `ScreenToWorld` to its ground raycast. |
| `VrPieces.LaserEnd(worldPoint)` | Every frame: where Mouse pieces go. |
| `VrUiLog.MousePixel` | The game's mouse pixel, for the piece logger. |
| UI tab → "Log UI pieces" | Writes `<config>/vrui-pieces.txt`: every UI object near the top of each canvas, classed Screen, Mouse or World, with draft `RegisterPiece` lines. |

Each piece gets a row on the UI tab ("UI pieces"): Window, At the laser, In the
world, Hidden or Auto, kept in `[VrUi] PiecePlace <id>`.

Used by: ats and cataclismo (pieces); tph and tpm (windows only).

**Limits today:**
- Pieces are crops of one screen-sized texture, so overlapping UI shows
  through them.
- World and laser pieces show at most 24 elements each.
- Elements are a root's direct children.
- They are a frame behind the game.
- Whether cut rects are flipped vertically on some graphics APIs hasn't been
  checked (`VrPieces.Shown`).

Drawing each piece from its own canvas is bead vr-4j0.3.

### World signs: `VrWorldCanvas`

A small canvas of its own (a speech bubble, a prompt) shown as a sign at a
world point, in every UI mode.
- The game's hook calls `VrWorldCanvas.Place(host, item, anchor, size)` each
  time the game positions the item. It returns false when it can't; leave the
  item on the pane then.
- `Release(canvas)` and `ReleaseAll()` put canvases back.

Used by: deathsdoor.

### UI modes, and Popout / Cutout windows (2026-11-06)

- **Two choices on the UI tab** (names from the user):

  | Choice | Options |
  |---|---|
  | Layout (`VrUi.Mode`) | Panel, Arranged (moved while the VR menu is open) |
  | Windows (`VrUi.Windows`), for Arranged | **Popout**: each window's own UI alone, nothing else shows through<br>**Cutout**: cut from the one picture of the whole UI |

- **One Arranged mode** (bead vr-jhx, 2026-11-07; Free is gone):
  - Arranged shows a working copy of a layout (`free.txt` in the layouts
    folder). Picking a layout on Game UI loads it into the copy.
  - **While the VR menu is open** every window, button bar and (in Panel mode)
    the game's UI pane can be grabbed, and each has the pink outline
    (`UiOutline`: faint, brighter under the laser, solid while held, yellow
    when letting go would put it on a wrist). Closed, they stay put.
  - Save as new layout / Save over layout / Start over from layout / Delete
    layout, as before.
  - Once, on first run: a saved `Mode = Free` becomes Arranged and keeps its
    arrangement. Someone in the old Arranged starts from their layout (their
    old `free.txt` is kept as `free.before-arranged.txt`). `[VrUi] OneArranged`
    marks it done.
  - **Sims** that grab windows must open the VR menu first
    (`VrUi.SetMode(UiMode.Free)` alone no longer makes them movable).
- **What a game offers:**
  - `VrUi.Offer(UiMode.Arranged)`: windows round the table. Panel is
    always offered. `UiMode.Free` still builds and means Arranged.
  - `VrUi.OfferPopout()`: its windows can pop out. Passing `UiMode.Separate` to
    `Offer` does the same, for older calls. Where Popout is offered it is the
    default (`[VrUi] Windows`), and the user can choose Cutout.
  - `UiWindowDef.Cutout = true`: a window that doesn't work popped out stays a
    cutout.
  - A saved `Mode = Separate` (the first prototype) becomes Arranged with Popout,
    and its arrangement (`separate.txt`) is kept.
- **How Popout draws** (`VrPopout`; tests in `docs/notes/ui-piece-isolation.md`):
  - Each window's roots get a nested Canvas, a GraphicRaycaster and a spare layer.
  - Windows that don't overlap on the game's screen share a group and one clean
    picture they are cropped from; overlapping ones go in another group.
  - Each group is one more render of the UI camera itself, with its mask and
    target swapped and put back. Usually 1-2 extra renders, only on frames the
    UI camera renders.
  - The game sees no change, and switching to Cutout or Panel undoes it all.
    Roots that are themselves a root canvas are left alone.
- **URP renderers filter layers themselves** (found in TPM). A URP renderer
  draws only the layers in its opaque / transparent masks, so spare layers it
  leaves out give empty windows, with no error.
  - `VrUi.OfferPopout` widens every URP renderer's masks (not only the default: a UI camera can use another, as cataclismo's does) to every spare
    layer, **once, at load (main menu)**, through `VrPipeline.WidenRendererLayers`.
  - Never per mode: changing the masks makes URP rebuild the renderer, and TPM's
    museum then drew black until the level was reloaded.
  - The log line `[vrui] popout: N spare layers …; renderer '…' layers 0x… / 0x…`
    shows what was used.
- **A mod that moves nested canvases onto their root's layer will undo Popout.**
  TPM had one; it now skips unnamed layers 8 and up.
- **Clicks:** `UiHit.Window` says which window the laser is on. Windows that
  overlap on the game's screen are apart in VR, so filter the game's raycast
  results with `VrUi.Owns(hit.Window, gameObject)`. vr-4j0.15 moves this into a
  core laser.
- **Window names** (`VrLabels`): while the VR menu is open in Arranged, each window's `Title` (or its id)
  sits just above its top-left corner, in the menu's colours, sized with the
  window. UI tab toggle "Window names" (`[VrUi] WindowNames`, default on).
- **Pop-ups next to their opener:** `UiWindowDef.OpensFrom = "build"`,
  `OpensSide = OpenSide.Right / Left / Above / Below`. With no place in the
  layout, the window sits beside its opener, at the opener's size.

### Pins: the point that stays put when a window changes size (bead vr-ler, 2026-11-07)

A window's `Source` changes size when a list gets longer or a part opens. A
window kept by its centre then moves all four edges. Its **pin** is the one
point that stays put: one of `UiPin.TopLeft`, `Top`, `TopRight`, `Left`,
`Centre`, `Right`, `BottomLeft`, `Bottom`, `BottomRight`.

```csharp
VrUi.RegisterWindow(new UiWindowDef { Id = "orders", Title = "Orders", Roots = ..., Pin = UiPin.TopRight }); // a list that grows down
VrUi.RegisterWindow(new UiWindowDef { Id = "build", Title = "Build", Roots = ..., Pin = UiPin.Bottom });     // a bar that grows up
```

- **The game's default:** `UiWindowDef.Pin` (Centre if unset). It applies to every
  placement that doesn't say a pivot: a layout line with no `pivot=`, or a
  `UiPlacement` made in code without `PivotX` / `PivotY`.
  - The placement's `Position` is then where that point of the window goes.
  - A built-in layout that sets `PivotX` / `PivotY` keeps them, so to use the def's
    pin, leave the pivot out of the layout.
- **The player changes it while arranging** (VR menu open):
  - The window under the laser, or held, shows its nine pin points as small pink
    dots on its outline. The current one is larger and solid, and the one under the
    laser turns white.
  - The trigger on a dot pins the window there.
  - **The window doesn't move.** Its placement is re-expressed about the new point
    (`UiPlacement.MovePivot`). A window being held stays in the hand the same way.
  - Between the dots the trigger reaches the game as before. A hit on a dot is
    reported as `UiHitKind.Frame`, so the game gets no click there.
  - The log says `[vrui] 'orders' pinned at its top right (was centre)`.
- **Saved as `pivot=x,y`** in the layout (and `free.txt`), as before. Only a pivot
  that was set is written: one the player picked, or one a built-in layout set. A
  window that never said one follows the game's `Pin`, even if a later version of
  the mod changes it.
- **From code and sims:** `VrUi.SetPin(windowId, pin)` and `VrUi.PinOf(windowId)`.
- **Not pinned:** pop-ups beside their opener (`OpensFrom`) are placed afresh each
  frame from the opener, so they already keep beside it. Sparse World / Mouse
  pieces are screen-sized frames.

### A whole canvas of world UI: `VrWorldUi` (prototype, 2026-11-06)

`VrWorldCanvas` for many elements under one root canvas: markers, building
overlays, map hover info. The game's own UI objects are drawn in the world,
sharp and at their real world points, instead of being cropped from the UI
texture.

| Call | For |
|---|---|
| `VrWorldUi.Hold(host, rootCanvas)` | Turns the canvas world-space. Its rect stays screen-sized, so the game's clamps and layout still see a screen. `ScreenUiPanel` then leaves it alone; `Release` and `ReleaseAll` give it back. |
| `VrWorldUi.SignPoint(host, anchor)` | Where a sign for a world point goes (nudged toward the eye). **Best:** patch the game's own "world point to canvas point" helper to return this for a held canvas, so the game positions every element itself. |
| `VrWorldUi.Place(host, item, anchor)` | Or position one element from a postfix. |
| `VrWorldUi.AddGroup(() => parents)` | Every active child of these parents faces the head at a constant apparent size, every frame (`Size`; a screen of canvas spans 35°). |
| `VrWorldUi.Raycast(originWorld, dirWorld, out RaycastResult hit)` | Clicks. It aims `EventCamera` (the held canvases' event camera) down the laser and raycasts their `GraphicRaycaster`s. Send pointer events to `hit.gameObject` with position `VrWorldUi.EventPixel`. Compare `hit.distance` (world units) with the other laser targets. |

Rules:
- Elements must not be clamped to the screen by the game's code; skip or
  undo the clamp.
- The canvas draws on `VisualLayer`, depth-tested like scenery.
- Used by: ats-vr (WorldHUD), in progress.

## 6. Our own surfaces

### VR menu (`VrUiMenu`, `VrMenu`, `VrSettings`, `VrModes`)

Content:
- `VrMenu.Welcome(title, version, tips…)`, `VrMenu.Notice(() => text)`
- `VrMenu.HideTab`, `HideSection`, `HideRow`
- `VrSettings.Add(new ToggleSetting / NumberSetting / IntSetting / ChoiceSetting / ActionSetting(section, label, …) { When = () => … })`
- `VrSettings.PutOnTab(section, tab)`. Tabs: Welcome, Mode, UI, Controls,
  Record, Graphics, Game. Unplaced sections go to Game.
- `VrModes.Add(id, name, description).Option(setting).Controls(sections…)`
- `VrModes.Store(get, set)`, `VrModes.Changed`, `VrModes.InWorld`
- `MenuFaq.Add(question, answer)`: the game's own Help questions, shown first.

**Help text names the controls as bound now** (`HelpText`, 2026-11-07). Don't
write "click the left stick" or "B cancels": after Left-handed, Swap sticks, a
preset or a rebind that's wrong. Write a placeholder; the menu fills it in from
`VrInput` each time it builds the page. This works in `MenuFaq.Add` (question
and answer), `VrMenu.Welcome`'s tips (Home's first-time box and "More tips"),
and a row's `WithHelp`. Plain text still works as before.

| Placeholder | Gives (right-handed defaults) |
|---|---|
| `{press:id}` | what to do: "press A", "pull the right trigger", "click the right stick", "push the left stick left or right", "squeeze either grip" |
| `{bind:id}` | the control alone: "A", "the right trigger", "either grip", "the right stick click", "the left stick (up and down)" |
| `{press:id1,id2}` | several actions as one; one stick's directions merge (`{bind:turn.left,turn.right}` gives "the left stick (left and right)") |
| `{press?:id}` / `{bind?:id}` | optional: if none of the ids is registered and bound in this game, the whole sentence is left out |
| `{laser hand}` / `{other hand}` | "right hand" / "left hand" |
| `{laser side}` / `{other side}` | "right" / "left" (for "the {laser side} grip") |
| `{key:action\|F8}` | a **game** key: the key the game binds to its action now (`VrHotkeys.UseActions`), else the key after the `\|` ("F8", "Page Up") |
| `{{` / `}}` | literal braces |

- `id` is a `VrInput.Register` id: the game's own ("pause", "back") or the core's
  table actions (`table.move`, `turn.left`, `turn.right`, `zoom.in`, `zoom.out`,
  `pan.up`, `pan.down`, `pan.left`, `pan.right`, `table.recentre`).
- Swap sticks moves stick directions to the other hand, not stick clicks.
- A placeholder that starts a sentence gets a capital: `"{press:back} to go back."`
  gives "Press Y to go back.". Write `{bind:...}` where the control is the subject:
  `"{bind:pause} pauses."` gives "X pauses.".
- An unbound action reads "(not bound: see Controls)". An unknown id or
  placeholder is logged once (`[menu] help text: ...`); an unknown placeholder
  reads "(?)". Raw braces are never shown.
- Controls the core reads directly, not through an action, have no id: the chord
  (both grips and both triggers), carrying (the laser hand's grip), the laser's
  click (the laser hand's trigger). Name their hand with `{laser side}` / `{other hand}`.
- Gamepad games (Death's Door): buttons the game reads from the pad are the
  game's own mapping, not `VrInput`'s. Placeholders can only name the hand.

Opening and testing:
- The chord opens the menu (`VrInput`); `VrUi.ToggleMenu()` does it from code.
- `VrUiMenu.SetRendered(false)` leaves it out of one camera's picture.
- For sims: `VrUi.MenuPointOf(label)`.

**Font** (`VrFont`): the menu and the bar use the first font that works, in this order:
1. `VrFont.Font`, a `TMP_FontAsset` the mod sets (from its own bundle)
2. `VrFont.FontFile`, a `.ttf` / `.otf` path (Unity 6's TextMeshPro only)
3. TextMeshPro's default font
4. the game's text
5. any loaded font asset
6. an OS font (`VrFont.OsFamilies`; Unity 6 only)

The log says which one it used, or why there is none.

**Heading font** (Tilt Warp, the DVR display font; `fonts/TiltWarp.ttf`, SIL OFL): the menu's title, tabs, section headings and the Welcome title use it where there is one. Rows keep the body font, and without one everything looks as before.
- Unity 6 (TPM, Cataclismo): ship `TiltWarp.ttf` next to the DLL and call `VrFont.UseModFolder(pluginDir)` (or set `VrFont.HeadingFontFile`).
- Older Unity: put the font in the mod's bundle and set `VrFont.HeadingLegacyFont` (it's made into a TMP font asset where TMP runs, and used as is for UI Text, e.g. Timberborn); or set `VrFont.HeadingFont` to a TMP font asset.

If the menu doesn't show, the log has one of these lines:
- `[vrui] menu is open but not drawn: …`: `IVrUiHost.Active` is false.
- `[vrui] no font for the VR menu yet …`: see Font above.
- `[vrui] menu failed to draw, closed: …`: an exception, with its stack.
- `[vrui] menu drawn on layer N …`: it is drawn. If it can't be seen, the eye
  cameras don't draw layer N (set `VrCore.VisualLayer`), or the material's
  shader is missing.

### Hotkey bars (`VrHotkeys`, `VrHotbar`)

```csharp
VrHotkeys.Set("Play").Group("Pan", 0, 0).Button("Up", "W", 1, 0).Button("Left", "A", 0, 1);
VrHotkeys.UseKeys((key, down) => /* press or release the key the way the game reads input */);
```

- `UseKeys` gets `(key, true)` every frame while a key is down and
  `(key, false)` once when it is let go. `ScrollUp` and `ScrollDown` get one
  `(key, true)` per notch.
- **Each set is its own bar** (2026-11-06), switched on and off by itself.
  - UI tab, "Hotkey bars": a row per set, plus "Group names".
  - Kept in `[VrUi] HotkeysOn <set>`. The first time, the set the old single bar
    showed is on.
  - From code: `VrHotkeys.IsOn(set)` and `SetOn(set, bool)`.
  - `Current` (the first set showing) and `Shown` (any showing) still work for
    older callers.
- **Moving a bar:** each moves while the menu is open, outlined in pink.
  It can be worn on a wrist, and its place and size are kept per set
  (`[VrUi] HotkeysPlace <set>`).
- For sims: `VrUi.HotbarPointOf(label)` finds a button on any bar showing.

#### Pictures on buttons (2026-11-07)

```csharp
VrHotkeys.Set("Play").Group("Rotate", 0, 0)
    .Button("Rotate left", "Q", 0, 0, icon: "rotate-ccw")
    .Button("Rotate right", "E", 1, 0).Icon("rotate-cw")             // or set it after
    .Button("Speed", "Space", 2, 0, icon: "fast-forward").Art(FindSpeedSprite);
```

- **What a button shows**, the first that works:
  1. `art` / `.Art(...)`, the game's own sprite, a `Func<Sprite>`. It is drawn in its own
     colours, its aspect kept.
  2. `icon` / `.Icon(...)`, a `VrIcons` name (the list is in `icons/README.md`), drawn cream
     at 34 px on the 64 px button.
  3. The label as text, as before. An icon that is missing is logged.
- **The label stays the button's name** in every case: in the log, and in `HotbarPointOf`
  for sims.
- **Game art is live** (2026-11-07). The bar asks the `Func` four times a second.
  - When it gives another sprite, that button's picture is swapped in place, with no
    redraw of the bar. That covers a slot whose skill was changed, a sprite that loaded
    late, and one Unity destroyed.
  - The same sprite again costs nothing (no allocation). So keep the `Func` cheap:
    find the game's `Image` once, then return its `sprite`; don't search each time.
  - A throw keeps what the button showed, and is logged once.
- **A live label:** `.LiveLabel(() => skillName)`, for the button added last. The text
  is drawn when there's no picture, and written in the log as `Skill 1 (Fireball)`. Null
  or empty falls back to the label. `HotbarPointOf` finds a button by its label first,
  then by what it shows.
- **`VrHotkeys.Refresh()`** asks every button now. Call it right after the game swaps a
  skill, so the bar doesn't wait up to a quarter of a second.

  ```csharp
  // Last Epoch: slot 1 is always key 1, whichever skill is in it.
  var slot1 = FindSkillSlotImage(0);   // the game's own Image, found once
  bar.Button("Skill 1", "1", 0, 0).Art(() => slot1 ? slot1.sprite : null).LiveLabel(() => SkillName(0));
  ```
- **Game art is only referenced, never copied.** The `Func` should return a sprite the game
  has already loaded at run time, for example one found on an `Image` in the game's own UI
  (its speed or rotate icon). Nothing is copied out of the game or shipped with the mod.
- **The old four-argument `Button(label, key, col, row)`** still builds, and draws text.

#### Buttons tied to the game's actions (2026-11-07)

A button that names a key breaks when the player remaps that key in the game. A
button can name one of the game's actions instead:

```csharp
VrHotkeys.Set("Play").Group("Pan", 0, 0)
    .Action("Pan forward", "Camera/PanUp", "W", 1, 0, icon: "arrow-up")   // action id, then the fallback key
    .Action("Pan left", "Camera/PanLeft", "A", 0, 1, icon: "arrow-left");
VrHotkeys.UseActions(id => /* the key name UseKeys understands, bound to id now; null if unknown */);
```

- **The key is looked up at each press** (`VrHotkeys.KeyFor(button)`), so a rebind made
  mid-session is followed. A key that is being held, or let go after `MinHold`, comes up
  as the key it went down as.
- **When the game can't say,** the button's own key is pressed. That covers no
  `UseActions`, a null or empty answer, or a throw (logged once). Pass `null` as the key
  when there is no sensible fallback; that button then does nothing until the game answers.
- **An action bound to `ScrollUp` / `ScrollDown`** turns the wheel, as a key button does.
  Everything else (`UseKeys`, `MinHold`, the wheel's repeat) is unchanged.
- **The game side is the game's.** The core stays generic, with no input-system code. For
  a game on Unity's Input System (TPM), the likely route is:
  - find the action with `InputActionAsset.FindAction(id)` (or `asset[id]`);
  - take the binding that applies to keyboard and mouse:
    `action.bindings[i].effectivePath`, which includes the player's overrides, for
    example `<Keyboard>/w`;
  - map it to the name the mod's key sender takes (`W`, `Space`, `ScrollUp`).

  `GetBindingDisplayString` gives the name for display, which suits a label but is
  localised, so it is a poor key name. The game agents write this per game.

### Carrying and resizing (the standard, 2026-11-06)

- **The holding hand's own physical stick** moves and resizes what it holds,
  whatever "swap sticks" says: the VR menu, a hotkey bar, the game UI pane, an
  Arranged window. Up / down pushes it away or pulls it in; right / left makes it
  bigger or smaller.
  - The core reads it itself (`VrInput.HandStick`).
  - `VrUi.UpdateGrab`'s stick arguments are ignored. A mod's "window farther /
    bigger" actions can go.
  - The stick is hidden from the game while it's carrying.
- **On a wrist:** everything but the VR menu can be dropped near the other
  hand's wrist to ride there: the bars, the pane (`PanePlace` gains a ninth
  field: 0 room, 1 off hand, 2 pointer hand) and the windows.

Used by: cataclismo, tpm.

### Text entry: the SteamVR keyboard (`VrKeyboard`, 2026-11-06)

- Call `VrKeyboard.Update(vr)` every frame, after `BeginFrame`. When a text
  field gets focus, SteamVR's keyboard opens with its text. Typing is copied
  into the field as it changes, Done enters it and leaves the field, and focus
  going elsewhere closes the keyboard.
- It reads SteamVR's event queue, so nothing else in the mod should.
- TextMesh Pro fields work out of the box (`TmpTextFields`). A UI Toolkit game
  sets `VrKeyboard.Fields` to its own `ITextFields` (timberborn-vr).
- `ITextFields.Set` must leave the cursor at the end with nothing selected. A
  field that selects all when its text is set otherwise has the next key press
  replace everything (the Timberborn bug).
- Replaces the copies in tph-vr, tpm-vr and timberborn-vr.

### Input (`VrInput`)

- `Register(id, name, section, group, defaults…)`: the defaults are
  `"Pointer.Trigger"`, `"Off.Grip"`, `"Pointer.StickUp"`… The group is the
  context the action is live in.
- Reading: `Pressed`, `Held`, `Released`, `Value`, `HeldOnSide`.
- `ChordFired` (undo whatever the chord's grips started), `SwapSticks`, presets.
- The chord (both grips and both triggers) is the core's; actions can't take it.

## 7. The rig, diorama, capture and pipeline

| Piece | Hooks |
|---|---|
| `VrRig(vr, host, rigConfig)` | `PrepareEye(cam)` (tweaks for each eye camera; call `CylinderCull.FitFar` from it), `Recenter`, `Turn`, `CancelGripDrag`, `Eye(i)`, `TrackingToWorld` / `WorldToTracking`. |
| `CylinderMask` | `ShaderSource` (the mod's bundle shader, built for its Unity version), `PropertyPrefix`, `FlipY`; `Update(show, cx, cz, radius, fade, colour, eyes)`. Used by: deathsdoor, tpm. |
| `CylinderCull` | `Update(on, cx, cz, radius)`, `EyesOnly`, `SkipLayers`, `RefreshSeconds`, `BoundsPerFrame`; `FitFar(eye, centre, radius, above, below, near, far)`. Used by: deathsdoor, tpm. |
| `SpectatorCam` | `Init(config)`, `Update(space, head, like, active)`, `PrepareCamera`. Used by: ror2, tpm. |
| `Recorder` | `Init(config)`, `ToggleVideo`, `TakePhoto`, `Update`; `FilePrefix`, `HeadsetEye`, `SaveHeadset`. Needs ffmpeg 8+ (`gfxcapture`, never `ddagrab`). Used by: ror2, tpm. |
| `VrPipeline` | `BeforeCamera` and `AfterCamera` events. Mono hooks them itself; **IL2CPP mods raise them** with `RaiseBefore` and `RaiseAfter` from their own hook. Also `CopyEyeSettings`, `RequireDepthTexture`, `SetShadows`. |
| Eye settings for a mod's own rig (vr-urz.1) | Each frame, per eye: `VrPipeline.CopyEyeSettings(eye, game)` (URP: post-processing, AA, volumes and the game's renderer, copied when the game camera changes; now on IL2CPP too), then `SetShadows(eye, on)` and `SetPostProcessing(eye, on)` for the mod's settings. `CopyRenderer(eye, game or null)` alone does cataclismo's MatchGameRenderer. On the built-in pipeline, shadows, renderer and post-processing are no-ops: PPv2 stays the mod's (tph). |
| `VrDesktop` (vr-urz.1) | The monitor's view off while VR shows the world. Replaces each pivot rig's `UpdateDesktopView` / `RestoreDesktopView` / `GameCullingMask`: `desktop.Update(main, blank: inWorld && !DesktopView)`, eyes' mask from `desktop.GameMask(main)`, `desktop.Restore()` when VR stops. |
| `VrVisuals`, `VrDrawOrder` | Unlit and overlay materials and primitives on `VrCore.VisualLayer`. Draw order: stage < mask (3500) < panels (4000) < menu (4010) < hands and laser (4050). |

## 7a. Start-up chores, diagnostics and Cinema (2026-11-06, bead vr-s4q)

These used to be copies in each mod; switch to the core's.

| Piece | Calls | Replaces |
|---|---|---|
| `VrBoot` | `if (session == null && VrBoot.StartDue() && VrBoot.GraphicsOk()) Start();`<br>Then each frame: `VrBoot.UncapFrameRate(session)` and `VrBoot.KeepFocus()`. | Each Plugin.cs's retry loop, D3D11 check, `UncapFrameRate` and `KeepInputWhenUnfocused`. `KeepFocus` sets runInBackground, UGUI's focus, and the Input System's `IgnoreFocus` when the game has one. |
| `VrRecon` | Each frame while a debug setting is on: `VrRecon.Tick(() => modKey, new[] { "HUD", "WorldHUD" }, sb => …)` | Each mod's `Recon.cs`. It dumps the cameras (with pipeline data), the renderer's layer filter, root canvases, the input module, and the structure under the named UI roots. |
| `VrCinema` | Each frame: `VrCinema.Update(inCinema, () => gameCamera, panel.Panel)`, or `Update(on, gameCamera, position, rotation, width)`.<br>`PrepareCamera` tweaks the mirror camera (tpm: GI). | Each mod's `VrCinema.cs`. Its screen uses `VrVisuals.Screen` (opaque) in the `VrDrawOrder.Screen` slot. |

## 7b. Headset-free tests: the sim toolkit and `tools/sim-common.sh` (2026-11-06, bead vr-s4q.4)

Each mod had its own copy of these. Move over one mod at a time (see "Migrating
a mod's sim" below). The game-specific parts stay in the mod: what the script
does, finding the game's buttons, input injection.

### C#: `SimRunner`, `SimHands`, `SimShots` (`src/Sim/`), `TexturePng` (`src/Capture/`)

| Piece | Calls | Replaces |
|---|---|---|
| `SimRunner` | `runner.StartOnce(Run(...))`, then each frame `runner.Step()`.<br>A script is an `IEnumerator<float>`: `yield return 1.5f` waits 1.5 s (unscaled time), `yield return 0` waits a frame.<br>`yield return runner.Nest(Steps())` runs a nested sequence to its end. Nested sequences stack, so one can nest another.<br>`Stop()`, `Running`, `Finished`.<br>Steps: `SimRunner.Frames(n)`, `Until(cond, timeout)`, and the last step `Finish(quitAfterTest)`, which logs `[sim] script finished` and quits. `GiveUp(reason, quit)` stops early.<br>Unity coroutine helpers: `WaitCo(s)`, `FramesCo(n)`. | The `script`, `nested`, `resumeAt`, `Step`, `Idle` / `Done` in each SimScript, and the QuitAfterTest ending. |
| `SimHands` | `hands.PointerPitch = () => cfg.PointerPitch.Value;`<br>Set `hands.Left` / `hands.Right`: `Pose`, `Trigger`, `Grip`, `StickX/Y` (held); `PrimaryPress`, `SecondaryPress`, `StickClick`, `Flick` (one frame).<br>Each frame, after `Step`: `hands.Apply(vr)`, which calls `VrHand.Simulate` for both hands.<br>Aiming the right hand's laser: `AimAtTracking(point[, from])`, `AimAtWorld(rig, world)`, `AimAtMenu("Graphics")`, `AimAtHotbar(label)` (the last two return false if the label isn't there).<br>Clicking, nested: `hands.Click(point)`, `ClickMenu(label)`.<br>Two-hand gestures: `Spread(half, twist, grip)`; for scripts that call `VrHand` directly, `SimHands.Spread(l, r, …)` / `SpreadCo(…, frames)`.<br>Rest poses: `LeftRest`, `AimFrom` (0.15, 1.1, 0.25). | `rightPose`, `trigger`, … plus `AimAt`, `AimAtTracking`, `AimAtMenu`, `ClickSteps`; deathsdoor and ror2's `Hands(l, r, half, twist, grip, frames)`. |
| `SimShots` | At load: `SimShots.Dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "atsvr-shots")`, and `SimShots.UiTexture = () => panel?.Texture`. Optional: `SaveRightEye`, `SaveDesktop` (default on).<br>Script: `SimShots.Request("01_table")`, or `Request(name, light: true)` (left eye and UI only).<br>Mod, once a frame where the eye textures hold a finished frame: `SimShots.CaptureIfRequested(left, right)`.<br>It saves `name_eye.png`, `_eyeR`, `_ui` and `_desktop`.<br>tpm's extras: `SaveEye(path)`; the capture countdown (`StartCountdown`, `ButtonText`, `CaptureDelay`); the `capture.txt` request file in `Dir`.<br>`SimShots.Save(rt, "x.png")` saves at once and returns the readback, to count pixels in. | Each mod's `Screenshots` class. |
| `TexturePng` | `Save(rt, path, alpha = true, keep = true)` writes `.png`, or `.jpg` at quality 85, by the file's extension. Also `Read(rt)`, `Write(tex, path)` and `Release()`. | Every RenderTexture readback, the Recorder's included. |

The usual frame, once the script runs:

```csharp
runner.StartOnce(Run(vr, rig, cfg));
runner.Step();
hands.Apply(vr);
// ... and after the eyes render: SimShots.CaptureIfRequested(rig.LeftTarget, rig.RightTarget);
```

The classes sit in `Dvr` like the rest of the core. A mod's own `SimScript` or
`Screenshots` still wins inside the mod's namespace, so nothing clashes during a
move.

### Shell: `tools/sim-common.sh` and `tools/wait-and-run.sh`

The mod's `tools/run-sim-test.sh` sets the variables and calls the steps:

```bash
#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "${DVR_CORE_DIR:-$ROOT/../../core/dvr-unity-core}/tools/sim-common.sh"
GAME_DIR="/d/SteamLibrary/steamapps/common/Against the Storm"
GAME_EXE="Against the Storm.exe"
PROJ="$ROOT/src/AtsVr"
CFG="$GAME_DIR/BepInEx/config/dvr.ats.cfg"
LOG="$GAME_DIR/BepInEx/LogOutput.log"
SHOTS="$GAME_DIR/BepInEx/atsvr-shots"
SAVES="$(cygpath -u "$USERPROFILE")/AppData/LocalLow/Eremite Games/Against the Storm"
sim_before_restore() { cp -f "$SAVES/Player.log" "$GAME_DIR/BepInEx/Player-sim.log" 2>/dev/null || true; }

sim_init
sim_check_idle
sim_build
sim_snapshot
sim_switch_on SimulateVr RunTestScript QuitAfterTest
sim_set_args "$@"                                    # Key=value from the command line
sim_launch powershell -File "$(cygpath -w "$ROOT/tools/launch.ps1")"
sim_wait_finished
sim_results '\[sim\]|\[menu\]|Exception|Error'
```

| Variable | |
|---|---|
| `GAME_EXE` (required) | The process image name, as tasklist shows it. |
| `PROJ` (required) | The mod's project folder, for `sim_build` (`dotnet build -c Release`, which deploys). |
| `CFG` (required) | The mod's config file. The switches go in `[Debug]`; `CFG_SECTION=""` for an ini without sections (timberborn). |
| `SAVES` (required) | The save folder. All of it is snapshotted and put back. It must be absolute and specific: a home, `AppData`, `Documents` or `steamapps` folder is refused. |
| `GAME_DIR`, `LOG`, `SHOTS` | The install folder, the log with the `[sim]` lines, the shots folder (cleared before launch). |
| `SAVES_CHECK`, `SAVES_NONEMPTY` | A path that must exist in `SAVES` (deathsdoor: `SAVEDATA`; ror2: `remote/UserProfiles`), and a folder whose snapshot mustn't be empty. |
| `SAVES_REGISTRY` | A registry key to export and put back exactly: cleared, then the snapshot imported and checked, so values a run added are removed (cataclismo, deathsdoor). |
| `SIM_GUARD_KEY` | The default `SimulateVr` means no launch unless `SimulateVr = true` is in `CFG`. Set it empty for a test that doesn't simulate (deathsdoor's angle tour). |
| `SIM_DONE_PATTERN`, `WAIT` | The line that ends the wait (default `\[sim\] script finished`, grep -E), and how long to wait for it (default 600 s). |
| `SIM_WAIT_BUSY` | Minutes to wait for another game to close. The default, 0, stops at once with exit 2. |
| `SKIP_BUILD`, `DVR_CORE_DIR`, `CLEAR_LOG=0`, `START_WAIT` | Test what's already deployed; build against another core copy; keep a log the game owns (Player.log); seconds to wait for the process (default 90). |

What the steps guarantee:
- **Busy check.** `DVR_GAME_EXES` is the one list of every DVR game's process;
  add new games there. Only one game may run on this PC.
  - The check runs before the build, again before the snapshot, and again just
    before launch.
  - A run lock (`$TMP/dvr-sim.lock`) keeps two sims apart from snapshot to
    restore.
  - If tasklist or grep fails, the PC counts as busy.
  - `wait-and-run.sh <command>` waits for the PC to be free, then runs the command.
- **Saves.** The snapshot is checked against the folder (`diff -r`) before
  anything changes.
  - The restore runs from the EXIT trap, also on Ctrl-C, TERM and HUP. Don't set
    your own EXIT trap: define `sim_before_restore` / `sim_after_restore`.
  - The restore closes only the instance it started (by PID; gracefully, then
    forced).
  - It copies back only if something changed: the run's own state is moved to
    `after-run/` first, and the result is compared again.
  - It leaves the saves alone, keeps the snapshot and exits non-zero if:
    - someone else's instance of the game is running;
    - the game wasn't launched but the saves changed;
    - its own instance won't close;
    - the copy doesn't verify.
- **Config.** `CFG` is put back as it was (deleted if it didn't exist). Any test
  switch the saved copy had on (from an earlier killed run) is turned off.
  CRLF files keep their line ends.

### Migrating a mod's sim

One mod at a time, by that game's own session:

1. **Shell first.** Rewrite `tools/run-sim-test.sh` on `sim-common.sh` as
   above, keeping the mod's extras as hooks or plain lines between the steps:
   - deathsdoor: `steam_appid.txt`, `SIM_GUARD_KEY=""`;
   - cataclismo: `SAVES_REGISTRY`, `SIM_WAIT_BUSY`;
   - timberborn: `CFG_SECTION=""`, `CLEAR_LOG=0`.

   Delete the mod's own `wait-and-run.sh` and call the core's.
2. **Dry-run the backup and restore before trusting it, without the game.**
   Copy the script to a scratch folder and point `SAVES`, `CFG` and `GAME_DIR`
   at a copy (`cp -a`) of the real ones. Replace the launch with a harmless
   stand-in.
   - **Use a uniquely named copy of ping** (e.g. `cp /c/Windows/System32/PING.EXE
     "$scratch/mygame-dry.exe"`, `GAME_EXE=mygame-dry.exe`). Plain `PING.EXE`
     or `timeout.exe` collide: a system service may run `PING.EXE`, and other
     agents' dry runs may be running theirs. Both count as "someone else's
     game".
   - **Use your own `SIM_LOCK`** for the dry run, too.
   - **With `SAVES_REGISTRY`, use a unique throwaway key** (e.g.
     `HKCU\Software\DvrSimDry_<mod>_<random>`), never the game's, and delete it
     afterwards.

   Then check:
   - change a file in the copy mid-run, and the copy comes back identical
     (`diff -r`);
   - stop it mid-run and it restores too. In a non-interactive shell a
     background job ignores SIGINT, so use `kill -TERM` (the same trap);
   - a second instance of the stand-in started mid-run leaves the copy alone
     and keeps the snapshot (exit 3);
   - `cmp` shows the config identical;
   - with `SAVES_REGISTRY`: a value **added** mid-run is gone after the restore
     (the key is cleared, then the snapshot imported, and the result checked).

   Only then run it for real, with the user's go-ahead (the user runs the games).
3. **C#.** Swap in the toolkit piece by piece, building after each:
   - `Screenshots` → `SimShots`: keep the old file names, so earlier shots compare;
   - the stepper → `SimRunner`;
   - the hand fields and aiming → `SimHands`.

   Check the sim's `[sim]` lines and shots match a run from before the move.
4. Bump nothing: the toolkit is additive (patch level).

## 7c. The standard table rig: `TableRig` (vr-urz.2; design in [RIG.md](RIG.md))

Diorama, Turntable and Orbit, the same controls in every game, every tunable a
per-game setting. It doesn't touch `VrRig`, which stays for the mods that
haven't moved yet.

**1. A host.** Subclass one (`src/Rig/RigHost.cs`):

| Host | Must give | May override |
|---|---|---|
| `PivotHost` (builders: Diorama, Turntable) | `GameCamera`, `InWorld`, `TryGetPivot(out focal, out yaw)`, `SetPivot(focal, yaw)` (sideways; return where it really went) | see "The game's hooks" below |
| `CharacterHost` (Orbit) | `GameCamera`, `InWorld`, `TryGetFocus(out feet, out grounded)` | `GripsBusy`, `Taken`. `CharacterHost.From(dioramaHost, () => inWorld)` wraps an existing `IDioramaHost`. |

**The game's hooks** (all optional, read every frame):

| Hook | For |
|---|---|
| `Taken` (any host) → `TableInputs` flags `Turn`, `Zoom`, `Pan` | The game has these inputs this frame, and the rig doesn't use them. tpm: `All` while an item is being placed (the pointer stick turns it). timberborn: `Turn` while a building is being placed. |
| `Sticks(TableSticks)` → `TableInputs` | Every frame, the sticks (turn axis, snap flicks, zoom axis, pan), less what `Taken` took. **Echo:** press the game's keys and return `None`, and the rig still acts (tpm holds Q / E and its zoom keys for its tutorial). **Drive:** press them and return what the game did itself, and the rig skips those (cata: `Turn \| Zoom`). |
| `GameTurns` | With a game-driven turn: the table follows the game's yaw. |
| `GameZoomDistance` → `float?` | With a game-driven zoom: the game camera's zoom distance while the table should follow it (cata: while zooming or framing a shot), else null. The scale changes by the same ratio and is saved when it settles. |
| `Pan(worldDelta)` | True: the game panned with its own keys (ats, cata, tph). False: the core moves the pivot. |
| `FollowGroundHeight` | The table's height follows `focal.y` whenever no grip holds it (timberborn: return the eased terrain height under the target as `focal.y`). |
| `Turned(degrees)` | The table turned (clockwise from above), for an echo. |
| `GripsBusy` | The game wants the grips. |

**2. Load:** after `VrUi.Init` and `VrInput.Init`, before `VrInput.Load`:

```csharp
// Pivot games: Diorama's and Turntable's sticks (off stick turns and zooms, pointer stick pans or
// moves the circle). This replaces Register + RegisterPan, so drop the mod's own pan.* actions.
TableActions.RegisterTableModes("Playing");
// Character games: Register("Playing") (pointer stick turns and zooms, RoR2), or
// Register("Playing", stickBindings: false, recentre: "Pointer.StickClick") when the pointer stick aims (DD, RE2).
// Both Register and RegisterTableModes take an optional recentre binding (unbound by default).
rig = new TableRig(vr, Config, host)
    .Table(t => { t.TableHeight = 0.75f; }, e => e.EyeResolution = 1f,
           new OldKey("TableHeight", "VR", "TableHeight", 0.9f),
           new OldKey("TableDistance", "VR", "TableDistance", 0.6f),
           new OldKey("FollowGameCamera", "VR", "FollowFocalPoint", true),
           new OldKey("DesktopView", "VR", "DesktopView", false),
           new OldKey("EyeResolution", "VR", "EyeResolution", 1f))
    .Offer(RigMode.Diorama, t => { t.Scale = 40; t.MinScale = 5; t.MaxScale = 300; },
           new[] { "Settlement" },             // the game's own action sections used in this mode
           new OldKey("Scale", "VR", "Scale", 40f))
    .Offer(RigMode.Turntable, t => { t.Scale = 40; })
    // tpm only: its old view setting, once.
    .OldMode("Diorama", "View", "VrView", ("VrView", RigMode.Diorama), ("Diorama", RigMode.Turntable), ("Tabletop", RigMode.Turntable));
rig.PrepareEye = cam => { /* the game's own: GI, PPv2, post-processing (VrPipeline.SetPostProcessing) */ };
```

- **Defaults:** set any `TableTuning` field in the `Offer` lambda. The
  fields are listed in RIG.md section 2; game-only limits such as `MinScale`,
  `MaxScale`, `SnapDistance` and `CutAbove` are set there too.
  - `Background` is per mode: Light, Dark, Passthrough, or `Game` (the game
    camera's own clear). Diorama defaults to `Game` and Turntable to `Light`;
    tpm sets its Light / Dark for Diorama too.
- **Old keys:**
  - `OldKey(newField, oldSection, oldKey, oldDefault)`, with the old key's own
    type.
  - `OldKey.Value(newField, value, oldDefault)` for a value the mod read
    itself (timberborn's config.ini). DD's `CutSoftEdge` (a bool) becomes
    `OldKey.Value("Edge", soft ? "Soft" : "Sharp", "Soft")` in its Orbit offer.
  - Each one moves over once, and only if the player had changed it from
    `oldDefault`.
- **Saved in** `[Dvr.Table]`, `[Dvr.Diorama]`, `[Dvr.Turntable]`,
  `[Dvr.Orbit]` and `[Dvr.Graphics]`.
- **Mode tab:** the mode's rows come from the core, and the eye rows go on the
  Graphics tab. The chosen mode is `[VrUi] ViewMode`, with ids `Diorama`,
  `Turntable` and `Orbit`.
- **Late is fine:** a mod that makes the rig only once VR starts (DD's start
  retry) still gets its modes and rows, because `Offer` and `Table` mark the
  menu for a rebuild. Its actions must still be registered before
  `VrInput.Load`.

**3. Each frame:** `rig.Update()` takes the place of the mod's `rig.Update()`
in section 2's order (after `VrInput.Update`). Then submit with
`rig.SubmitEyes()` or `rig.QueuePreviousFrame()`, or hand `rig.Left` and
`rig.Right` to the mod's own submit.

**The rig does itself:**
- the grips, the sticks (turn, zoom, pan, Turntable's circle) and recentring.
  It never sets `VrInput.SwapSticks`, which is left for a player's "Swap
  sticks"; `ManagesStickSwap` does nothing now.
- Cinema outside a world.
- `VrDesktop`, the eye textures (`EyeResolution`), shadows, and the cut:
  `CylinderCull` with the frustum fit, or `CylinderMask` when the mod gave a
  shader. While a cut is on, the shadow distance is capped at the fitted far
  plane (`CapShadows`, default on).
- Hand markers when `HandMarkers = true` (ror2, DD).
- Cancelling a drag on the chord.

**The mod drops:** its `VrRig.cs`, and its stick code for turning, zooming and
panning the table (keep the game actions: placing, rotating a building).

**Read back and nudge:**
- `Mode`, `InWorld`, `Anchor`, `Yaw`, `TableCentre`, `CutRadius`, `Tuning`
  (this frame's values), `Eye(i)`, `TrackingToWorld` / `WorldToTracking`,
  `Desktop`, `Settings`.
- `Scale` is settable; call `SaveScale()` after a game key changes it (DD's
  F6 / F7).
- `Recenter()` re-anchors on the pivot, its height and yaw included. Cata
  calls it each frame while the game frames a shot.
- `EyeLayersOff` is the layers the eyes skip (default: UI, 5). Set it to 0
  for ror2 and DD, as their old rig had it.
- `SaveChanges = false` for sims.

## 8. Build switches

The build switches (`DvrRuntime`, `DvrPipeline`, `DvrEntityId`,
`DvrNoBepInEx`, `DvrCoreOpenVR`) and what each one defines are in the README.
`DvrPipeline` Custom: the mod brings its own `VrPipeline` (README, "Using it").

IL2CPP rules:
- Key objects by `VrCore.Key`, never by reference.
- No Unity delegates (`Camera.onPreCull`, mesh array setters).
- Denuvo games (tpm) allow no class injection: no custom MonoBehaviours.
