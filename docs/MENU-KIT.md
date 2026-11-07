# The menu kit: shapes, icons, the body font, scrolling

The building blocks the redesigned menu is drawn with ([menu-design/README.md](menu-design/README.md), bead vr-54x). All are in `src/UiKit/Menu/`, namespace `VrUiKit`.

They use no uGUI events and no EventSystem. The menu hit-tests the laser against its own rectangles, because class injection is ruled out in some games. Coordinates are canvas pixels from the top-left, as the menu's controls use.

## VrShapes: rounded rectangles

White sprites, made at run time and nine-sliced, so one sprite serves every size and `Image.color` tints it. They are cached by kind, radius and width, and made again if Unity destroys one (a scene change).

| Call | Gives |
|---|---|
| `Fill(radius)` | a filled rounded rectangle |
| `Border(radius, width = 2)` | a ring along the inside of the edge (CSS `border`; inner radius is radius − width) |
| `Highlight(radius, width = 2)` | the primary button's inset top highlight: tint it white at 14 %, over the fill |
| `Pill(height)`, `PillBorder(height)`, `Circle(diameter)` | radius half the height: the design's 999 (switches, the mode pill, the knob) |
| `Paint(go, sprite, colour)` | an `Image` on `go`: sliced, not a raycast target, with the stand-in material where the game has no `UI/Default`; a null sprite gives a plain rectangle |
| `Layer(parent, sprite, colour, name, inset)` | a child stretched over `parent`, painted: a border or highlight over a box |

- **Radii:** `Key` 6, `Segment` 10, `Button` 12, `Card` 14. Lines are `Line` = 2 px.
- **Scale:** one texel is one canvas pixel. `PixelsPerUnit` (100) must equal the canvas's `referencePixelsPerUnit`, Unity's default.
- **Edges:** antialiased over one pixel, from the signed distance at each texel's centre (`ShapeMath`, tested in CoreTests).
- **If it fails:** a failure is logged once, and `Paint` draws square boxes.

## VrIcons: Lucide icons

77 icons (the design's list; `info`, `sparkles` and the four `arrow-*`; and 31 for the button bars), as 48 px white PNGs in `icons/`. They come from `lucide-static` 0.460.0 (ISC, `icons/LICENSE-lucide.txt`) and are baked offline by `tools/bake-icons.sh`.

- `VrIcons.UseModFolder(dllDir)`: uses `<dir>/icons`, or `dir` itself. The menu calls it at start-up, and DvrUnityCore.props copies `icons/` next to the mod's DLL.
- `VrIcons.Get(name)`: a mipmapped sprite, cached, or null. A missing folder or icon is logged once.
- `VrIcons.Paint(go, name, colour)`: an `Image` with the aspect kept. It is switched off when there is no icon, so a row just has none.
- `VrIcons.All`: every name. A test keeps it equal to the bake script's list and to the files.
- **On the button bars:** `.Button(label, key, col, row, icon: "rotate-ccw")`, or the game's own sprite with `.Art(...)`. See docs/HOOKS.md, "Pictures on buttons".

## The body font: Atkinson Hyperlegible Next

Static TTFs at 400, 600 and 700 in `fonts/` (OFL). They are static because TextMesh Pro reads only a variable font's default instance.

- **Finding them:** `VrFont.UseModFolder(dir)` finds them, and Tilt Warp, in `dir` or `<dir>/fonts`.
- **`VrFont.Body(host, weight)`:**
  - gives the asked weight, else the nearest (CSS's rule: 600 tries 700, then 400);
  - else the menu's current font (`Get`), so games where TMP can't load a file keep today's look.
- **`VrFont.MakeText(..., weight: 600)`:** draws in the body font. Where only a lighter face stands in, TMP thickens it (it does from 700). UI Text turns bold from 600.
- **The font has no arrows (← → ↑ ↓):**
  - draw them as icons where you write the text;
  - for text the menu doesn't write (`VrInput.BindingText`), every body and heading font falls back to the menu's previous font, then to an OS font (Segoe UI, Arial).
- **A mod with its own assets:** it can put them in `VrFont.BodyFonts[weight]`.
- **Fonts from a bundle:** `VrFont.BodyLegacyFonts[weight]` (Font assets; a Font can only come from an AssetBundle), next to `HeadingLegacyFont`. One bundle serves two cases:
  - without TextMesh Pro (Timberborn), UI Text draws with them: the nearest weight, made bold only when the face standing in is lighter than 600;
  - where TextMesh Pro can't read a file (TPH, Death's Door), each is made into a TMP asset with `CreateFontAsset(font)`, as the heading is from `HeadingLegacyFont`. A file is tried first where there is one.

## VrScroll: a scroll area the menu drives

```csharp
var s = new VrScroll { Viewport = new Rect(120, 112, 1080, 688) };
s.ContentHeight = bottom + 28 - s.Viewport.y;      // after building the page
// each frame:
bool wantsStick = s.Update(laserPoint, triggerHeld, handStickY, dt);
// claim that hand's stick (VrInput.ClaimStick) while wantsStick, release it after
s.Apply(contentRoot);                               // or read s.Offset
// hit-testing a control in the scrolled content (its Rect as drawn at offset 0):
if (s.Hits(control.Rect, laserPoint)) ...
```

- **Rectangles to draw and hit-test:**
  - `Bar` (48 px wide, inside the viewport's right edge);
  - `UpArrow` and `DownArrow` (48 × 48, at the bar's ends; `ArrowSize = 0` for none);
  - `Track`;
  - `Thumb` (hit area, the bar's width) and `ThumbDrawn` (12 px, centred).
- **Clipping:**
  - `Hits(rect, p)` is true only inside the viewport and off the bar, at the scrolled place;
  - `Visible(rect)` (any overlap) is for skipping work;
  - `OnCanvas(rect)` and `ToContent(p)` convert between canvas and content.
- **Input:** `Update` does it all, or call the parts yourself:
  - `Press(p)` on trigger down: the thumb, an arrow, or the track, where the thumb's centre jumps there and is held;
  - `Drag(p)` and `Release()`;
  - `Hover(p)`;
  - `Stick(y, dt)`: dead zone 0.15, speed rate × push², up scrolls up, a coast on release (k = 6 /s), and it stops at the ends without bouncing;
  - `Wheel(notches)`;
  - `Tick(dt)`: coasting, and a held arrow repeats after 0.35 s;
  - `Scroll(px)`, `SetOffset(y)`, `ScrollIntoView(rect)`.
- **Only a press that starts on the bar grabs it.** A trigger held from a click on the page and dragged onto the bar does nothing. `Update` takes presses on the bar, so don't also route them as clicks.
- **State:**
  - `WantsStick`: the laser is over the area, or holding the thumb, and there is something to scroll. Content that fits never takes the stick from the table.
  - `Moving` and `Dragging`.
- **Tuning:** the fields `StickRate`, `StickCurve`, `StickDeadZone`, `Coast`, `ArrowStep`, `ArrowHoldDelay`, `ArrowHoldRate`, `WheelStep`, `MinThumb`, `BarWidth`, `ThumbWidth` and `ArrowSize`.
- **No mask:** `RectMask2D` clips through `UI/Default`'s clip-rect keyword, which the stand-in shaders lack. So draw opaque layers over what scrolls out, as the shell does.

## Older games

What decides the body font is whether the game's TextMesh Pro has a public `CreateFontAsset(string path, ...)`. Checked 2026-11-06 by disassembling each game's `Unity.TextMeshPro.dll`:

| Game | Unity | Its TextMesh Pro | Shapes, icons | Body font |
|---|---|---|---|---|
| TPM | 6000.0.63 | has the path overload | yes | yes (a Unity test draws it, arrows from the fallback) |
| ATS | 2021.3.45f2 | has the path overload (`m_SourceFontFilePath`; TMP 3.2) | yes | yes, by the same code (not seen in the game yet) |
| TPH | 2021.3.45f1 | `CreateFontAsset(Font)` only (TMP 3.0) | yes | no: today's font, unless the mod gives `BodyFonts` |
| Death's Door | 2019.4.29f1 | `CreateFontAsset(Font)` only (TMP 2.x) | yes | no, as TPH |
| Timberborn | | no TMP Settings: UI Text | yes (stand-in material) | no: Unity's built-in font |

### A body font for TPH and Death's Door: the options (not built)

Their TextMesh Pro needs a `UnityEngine.Font` object, and there's no public way to make one from a TTF at run time.

- **A Font from a file: no way.**
  - `new Font(name)` makes an empty font, with no way to give it a file's data.
  - `RequestCharactersInTexture` only draws from data the font already has.
- **`Font.CreateDynamicFontFromOSFont`: installed fonts only.** We'd have to install Atkinson on the player's system. No.
- **An AssetBundle with the three TTFs as Font assets (recommended).**
  - Build it once with Unity 2019.4.29f1, Death's Door's version, which is installed here. Death's Door already builds a shader bundle in `tools/ShaderBundle`.
  - A bundle from an older Unity loads in a newer player, so one bundle should serve TPH (2021.3) too. Check that in TPH.
  - Then `TMP_FontAsset.CreateFontAsset(font)` gives a dynamic asset, and the mod puts it in `VrFont.BodyFonts[weight]`. Or the core loads the bundle itself next to the fonts, if it ships.
- **A static atlas baked offline.**
  - The SDF atlas and glyph metrics for the menu's characters, made in the Unity 6 test project and put together into a `TMP_FontAsset` at run time.
  - Possible on Mono: Unity's `FontEngine.LoadFontFace(path)` is public in both games, and the glyph rendering is internal, reachable by reflection.
  - But it means setting TextMesh Pro's private fields, whose names differ between TMP 2.x and 3.0, and the glyph set is fixed (the fallback covers the rest). More code and more fragile than a bundle.

- **IL2CPP:** everything compiles. If `ImageConversion.LoadImage` was stripped from a game, the first icon logs it, and the menu draws without icons.
