#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// Hotkey bars: one per hotkey set the user has switched on (UI tab, "Hotkey
// bars"), each its own see-through panel (no background, only the buttons and
// group labels), drawn like the VR menu with stock uGUI on a canvas only its
// own camera renders. Each bar is carried like a window
// (VrUi.CarryPanels) while the VR menu is open: the grip
// on it carries it, turning with that hand unless 'Held windows' is Face you,
// and letting go with any part of it near the other hand's wrist (its buttons
// go yellow) puts it on that wrist, by the same rule as windows. Each keeps its
// own place and size. Its buttons press the game's keys
// (not while the VR menu is open, when the bars are being arranged).
static class VrHotbar
{
    const int Cell = 72, ButtonSize = 64, Pad = 8, LabelHeight = 28;
    const int IconSize = 34, ArtSize = 44; // a VrIcons icon / the game's own sprite, centred on a button
    const float ArtLook = 0.25f; // how often a bar asks its live buttons for their art and label
    const int Layer = 5; // UI: the eye cameras never render it
    static readonly Vector3 CameraPosition = new(0, -140, 0);
    const float MetresPerPixel = 0.42f / 640; // the VR menu's scale

    static readonly Color ButtonColour = new(0.17f, 0.15f, 0.22f, 1f);
    static readonly Color DownColour = new(0.78f, 0.05f, 0.35f, 1f);
    static readonly Color TextColour = new(0.96f, 0.94f, 0.91f, 1f);
    static readonly Color LabelColour = new(0.93f, 0.85f, 0.29f, 1f);
    static readonly Color SnapColour = UiOutline.Snap; // as a window's frame: letting go puts it on a wrist

    enum Mount { Free, Wrist, PointerWrist }

    sealed class Control
    {
        public Rect Rect;
        public Image Image;
        public Color Color;
        public HotkeyButton Button;
        public Sprite Art; // the game sprite drawn on it, or null
        public Image Picture; // the Image showing Art: its sprite is swapped in place
    }

    sealed class Bar
    {
        public HotkeySet Set;
        public int Index;
        public Camera Cam;
        public RenderTexture Texture;
        public Canvas Canvas;
        public Transform Content;
        public GameObject Quad;
        public Material Material;
        public int PixelWidth, PixelHeight;
        public bool Dirty = true, Restored, Carried, Snapping;
        public UiOutline Outline;
        public Mount Mount;
        public Pose FreePose, WristRel, TrackingPose, WorldPose;
        public float Scale = 1;
        public readonly List<Control> Controls = new();
        public Control Hovered;
        public float NextArtLook;
        public float Width => PixelWidth * MetresPerPixel * Scale;
        public float Height => PixelHeight * MetresPerPixel * Scale;
        public bool Visible => Quad != null && Quad.activeSelf;
    }

    static readonly List<Bar> bars = new();
    static Bar hoverBar, nearBar, carriedBar;
    static bool carriedByOffHand;
    static TMP_FontAsset font;
    static bool fontLooked;

    // Any bar showing (for the mods' sims).
    public static bool Visible => bars.Any(b => b.Visible);
    /// <summary>All bars hidden (the Recorder's countdown and shot), whatever else says.</summary>
    public static bool Suppressed;
    // Arranging: while the VR menu is open the bars are solid and their buttons don't press.
    public static bool Placing => VrUiMenu.Open;
    public static void MarkDirty() { foreach (var b in bars) b.Dirty = true; }
    // Ask the live buttons now, not at the next look (VrHotkeys.Refresh).
    internal static void LookNow() { foreach (var b in bars) b.NextArtLook = 0; }

    // Every frame from VrUi.Update.
    public static void Update(IVrUiHost host)
    {
        VrHotkeys.Update(hoverBar?.Hovered?.Button);
        for (int i = 0; i < VrHotkeys.Sets.Count; i++)
        {
            var set = VrHotkeys.Sets[i];
            var bar = bars.FirstOrDefault(b => b.Set == set);
            if (bar == null) bars.Add(bar = new Bar { Set = set, Index = i });
            if (Suppressed || !host.Active || !VrHotkeys.CanPress || !VrHotkeys.IsOn(set)) { Hide(bar); continue; }
            if (!bar.Restored) { Restore(host, bar); bar.Restored = true; }
            if (!Ensure(host, bar)) continue;
            LookAtLive(bar);
            if (bar.Dirty) Rebuild(bar);
            bar.TrackingPose = bar.Carried || bar.Mount == Mount.Free ? bar.FreePose
                : (bar.Mount == Mount.Wrist ? HandDrag.OffHand(host) : HandDrag.PointerHand(host)).Compose(bar.WristRel);
            bar.Cam.enabled = true;
            bar.WorldPose = host.TrackingToWorld(bar.TrackingPose);
            bar.Quad.SetActive(true);
            bar.Quad.transform.SetPositionAndRotation(bar.WorldPose.UnityPosition, bar.WorldPose.UnityRotation);
            bar.Quad.transform.localScale = new Vector3(bar.Width * host.WorldScale, bar.Height * host.WorldScale, 1);
            ShowSnap(bar, bar.Carried && SnapsOn(host, bar) != null);
            // While the VR menu is open the bars can be moved: the pink outline says so.
            if (Placing)
            {
                bar.Outline ??= new UiOutline("Hotbar " + bar.Set.Name);
                var state = bar.Snapping ? UiOutline.State.Snap : bar.Carried ? UiOutline.State.Held
                    : bar == hoverBar ? UiOutline.State.Hover : UiOutline.State.Idle;
                bar.Outline.Show(host, bar.WorldPose, bar.Width * host.WorldScale, bar.Height * host.WorldScale, state);
            }
            else bar.Outline?.Hide();
        }
    }

    // While carried: the buttons go yellow when letting go would put the bar on a wrist.
    static void ShowSnap(Bar bar, bool snapping)
    {
        if (bar.Snapping == snapping) return;
        bar.Snapping = snapping;
        foreach (var c in bar.Controls)
            c.Image.color = snapping ? SnapColour : c == bar.Hovered ? Color.Lerp(c.Color, Color.white, 0.18f) : c.Color;
    }

    // The wrist the carried bar would go on if let go now (the hand not holding it), as for
    // windows: any part of the bar within VrUi.SnapReach of that wrist. Null: none.
    static Mount? SnapsOn(IVrUiHost host, Bar bar)
    {
        var other = carriedByOffHand ? HandDrag.PointerHand(host) : HandDrag.OffHand(host);
        if (HandDrag.DistanceTo(bar.FreePose, bar.Width, bar.Height, VrUi.WristPoint(other)) >= VrUi.SnapReach) return null;
        return carriedByOffHand ? Mount.PointerWrist : Mount.Wrist;
    }

    static void Hide(Bar bar)
    {
        if (bar.Quad != null) bar.Quad.SetActive(false);
        if (bar.Cam != null) bar.Cam.enabled = false;
        bar.Outline?.Hide();
        if (bar == hoverBar) SetHover(null, null);
    }

    // The nearest bar on the laser. Only its buttons are solid, unless the bars are being arranged.
    public static bool Raycast(IVrUiHost host, NVec origin, NVec dir, out float distance)
    {
        distance = float.MaxValue;
        Bar best = null;
        Control bestControl = null;
        foreach (var bar in bars)
        {
            if (!bar.Visible) continue;
            if (!UiGeometry.QuadHit(bar.WorldPose, bar.Width * host.WorldScale, bar.Height * host.WorldScale, origin, dir, out float u, out float v, out float d)) continue;
            var p = new Vector2(u * bar.PixelWidth, (1 - v) * bar.PixelHeight);
            var hit = bar.Controls.FirstOrDefault(c => c.Rect.Contains(p));
            if (hit == null && !Placing) continue; // between the buttons the laser goes through (grab a bar by a button)
            if (d < distance) { distance = d; best = bar; bestControl = hit; }
        }
        SetHover(best, bestControl);
        return best != null;
    }

    static void SetHover(Bar bar, Control control)
    {
        if (hoverBar?.Hovered != null && (hoverBar != bar || hoverBar.Hovered != control))
        {
            hoverBar.Hovered.Image.color = hoverBar.Hovered.Color;
            hoverBar.Hovered = null;
        }
        hoverBar = bar;
        if (bar != null && control != null && bar.Hovered != control)
        {
            bar.Hovered = control;
            control.Image.color = Color.Lerp(control.Color, Color.white, 0.18f);
        }
    }

    // The trigger went down on a bar: press the button (not while the bars are being arranged).
    public static void Click(IVrUiHost host)
    {
        var b = hoverBar?.Hovered?.Button;
        if (Placing || b == null) return;
        VrHotkeys.Press(b);
        host.Log($"[hotbar] {hoverBar.Set.Name}: {b.Describe} ->{b.PressedKey}{(b.Action != null ? $" (action {b.Action})" : "")}");
    }

    // ---- carried (VrUi.CarryPanels) ----

    // Bars move while the VR menu is open, as windows do.
    static bool Movable => Placing;
    internal static bool CanCarry => carriedBar != null || (Movable && hoverBar != null);
    internal static Pose CarryPose => (carriedBar ?? hoverBar ?? nearBar)?.TrackingPose ?? Pose.Identity;

    // The nearest movable bar to a point (the other hand reaching for one); remembered for BeginCarry.
    internal static float DistanceTo(NVec point)
    {
        nearBar = null;
        if (!Movable) return float.MaxValue;
        float best = float.MaxValue;
        foreach (var bar in bars)
        {
            // A bar on the off hand's own wrist stays put, as windows do (that grip still moves the table).
            if (!bar.Visible || bar.Mount == Mount.Wrist) continue;
            float d = HandDrag.DistanceTo(bar.TrackingPose, bar.Width, bar.Height, point);
            if (d < best) { best = d; nearBar = bar; }
        }
        return best;
    }

    internal static void Resize(float factor) { if (carriedBar != null) carriedBar.Scale = Math.Clamp(carriedBar.Scale * factor, 0.5f, 2.5f); }

    internal static Pose BeginCarry(IVrUiHost host, bool byOffHand)
    {
        carriedBar = byOffHand ? nearBar : hoverBar;
        if (carriedBar == null) return Pose.Identity;
        carriedBar.Carried = true;
        carriedByOffHand = byOffHand;
        carriedBar.FreePose = carriedBar.TrackingPose;
        carriedBar.Mount = Mount.Free;
        host.Log($"[hotbar] carrying '{carriedBar.Set.Name}'");
        return carriedBar.TrackingPose;
    }

    internal static void CarryTo(Pose p) { if (carriedBar != null) carriedBar.FreePose = carriedBar.TrackingPose = p; }

    // Let go: near the other hand's wrist it goes on that wrist.
    internal static void EndCarry(IVrUiHost host, bool byOffHand)
    {
        var bar = carriedBar;
        carriedBar = null;
        if (bar == null) return;
        carriedByOffHand = byOffHand;
        var snap = SnapsOn(host, bar);
        bar.Carried = false;
        ShowSnap(bar, false);
        if (snap is Mount m)
        {
            var other = byOffHand ? HandDrag.PointerHand(host) : HandDrag.OffHand(host);
            bar.Mount = m;
            bar.WristRel = other.Inverse().Compose(bar.FreePose);
        }
        VrHotkeys.SavePlacement(bar.Set, Encode(bar));
        host.Log($"[hotbar] '{bar.Set.Name}' placed {(bar.Mount == Mount.Free ? "at " + bar.FreePose.Position : "on the " + (bar.Mount == Mount.Wrist ? "off" : "pointer") + " hand's wrist")}, size {bar.Scale:F2}");
    }

    // ---- placement, kept per set ----

    static void Restore(IVrUiHost host, Bar bar)
    {
        var parts = VrHotkeys.LoadPlacement(bar.Set).Split(' ');
        bar.Scale = parts.Length == 9 && float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? Math.Clamp(sc, 0.5f, 2.5f) : 1;
        if (parts.Length >= 8 && TryPose(parts, out var p))
        {
            bar.Mount = parts[0] == "wrist" ? Mount.Wrist : parts[0] == "pwrist" ? Mount.PointerWrist : Mount.Free;
            if (bar.Mount == Mount.Free) bar.FreePose = p; else bar.WristRel = p;
            return;
        }
        // First time: low and to the left, in front of the user, facing them; each set a little lower.
        var head = host.Head;
        var f = head.Forward;
        var flat = new NVec(f.X, 0, f.Z);
        flat = flat.LengthSquared() < 1e-4f ? NVec.UnitZ : NVec.Normalize(flat);
        var left = new NVec(-flat.Z, 0, flat.X);
        bar.Mount = Mount.Free;
        bar.FreePose = HandDrag.FacingHead(host, head.Position + flat * 0.45f + left * 0.32f + new NVec(0, -0.35f - 0.12f * bar.Index, 0));
    }

    static string Encode(Bar bar)
    {
        var p = bar.Mount == Mount.Free ? bar.FreePose : bar.WristRel;
        string F(float x) => x.ToString("0.####", CultureInfo.InvariantCulture);
        string m = bar.Mount switch { Mount.Wrist => "wrist", Mount.PointerWrist => "pwrist", _ => "free" };
        return $"{m} {F(p.Position.X)} {F(p.Position.Y)} {F(p.Position.Z)} {F(p.Rotation.X)} {F(p.Rotation.Y)} {F(p.Rotation.Z)} {F(p.Rotation.W)} {F(bar.Scale)}";
    }

    static bool TryPose(string[] s, out Pose p)
    {
        p = default;
        var f = new float[7];
        for (int i = 0; i < 7; i++)
            if (!float.TryParse(s[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return false;
        p = new Pose(new NVec(f[0], f[1], f[2]), NQuat.Normalize(new NQuat(f[3], f[4], f[5], f[6])));
        return true;
    }

    // ---- drawing ----

    static (float x, float y) CellPixel(HotkeyButton b) =>
        (Pad + (b.Group.Col + b.Col) * Cell, Pad + LabelHeight + (b.Group.Row + b.Row) * Cell);

    static bool Ensure(IVrUiHost host, Bar bar)
    {
        if (font == null && !fontLooked)
        {
            font = VrFont.Get(host);
            if (font == null && !VrFont.UseLegacy(host)) return false;
            fontLooked = font != null || VrFont.UseLegacy(host);
        }
        if (bar.Cam == null)
        {
            var go = new GameObject("VrUi Hotbar Camera " + bar.Set.Name);
            UiGeometry.Keep(go);
            go.transform.position = CameraPosition + new Vector3(0, 0, 20 * bar.Index);
            bar.Cam = go.AddComponent<Camera>();
            bar.Cam.clearFlags = CameraClearFlags.SolidColor;
            bar.Cam.backgroundColor = new Color(0, 0, 0, 0); // see-through between the buttons
            bar.Cam.nearClipPlane = 0.3f;
            bar.Cam.farClipPlane = 5f;
            bar.Cam.depth = -19;
            bar.Cam.cullingMask = 1 << Layer;
        }
        host.PrepareCamera(bar.Cam);
        if (bar.Canvas == null)
        {
            var go = new GameObject("VrUi Hotbar Canvas " + bar.Set.Name) { layer = Layer };
            UiGeometry.Keep(go);
            bar.Canvas = go.AddComponent<Canvas>();
            bar.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            bar.Canvas.worldCamera = bar.Cam;
            bar.Canvas.planeDistance = 1f;
            var holder = new GameObject("Content") { layer = Layer };
            var rt = holder.AddComponent<RectTransform>();
            bar.Content = rt;
            rt.SetParent(go.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
        if (bar.Quad == null)
        {
            bar.Quad = UiGeometry.MakeQuad("VrUi Hotbar " + bar.Set.Name, host.VisualLayer);
            bar.Material = host.MakeTexturedMaterial(Texture2D.whiteTexture);
            bar.Material.renderQueue = Dvr.VrDrawOrder.Menu;
            bar.Quad.GetComponent<Renderer>().sharedMaterial = bar.Material;
        }
        return true;
    }

    static void Rebuild(Bar bar)
    {
        bar.Dirty = false;
        bar.Snapping = false; // new buttons start in their own colours
        if (bar == hoverBar) SetHover(null, null);
        bar.Controls.Clear();
        for (int i = bar.Content.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(bar.Content.GetChild(i).gameObject);

        // Size the texture to the buttons.
        int w = Pad * 2 + ButtonSize, h = Pad * 2 + LabelHeight + ButtonSize;
        foreach (var g in bar.Set.Groups)
            foreach (var b in g.Buttons)
            {
                var (x, y) = CellPixel(b);
                w = Math.Max(w, (int)(x + ButtonSize + Pad));
                h = Math.Max(h, (int)(y + ButtonSize + Pad));
            }
        if (bar.Texture == null || w != bar.PixelWidth || h != bar.PixelHeight)
        {
            bar.PixelWidth = w;
            bar.PixelHeight = h;
            if (bar.Texture != null) { bar.Texture.Release(); UnityEngine.Object.Destroy(bar.Texture); }
            bar.Texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "VrUi Hotbar " + bar.Set.Name,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            bar.Texture.Create();
            bar.Cam.targetTexture = bar.Texture;
            bar.Material.mainTexture = bar.Texture;
            if (bar.Material.HasProperty("_BaseMap")) bar.Material.SetTexture("_BaseMap", bar.Texture); // URP Unlit
        }

        foreach (var g in bar.Set.Groups)
        {
            if (VrHotkeys.Labels && !string.IsNullOrEmpty(g.Label) && g.Buttons.Count > 0)
            {
                float lx = Pad + (g.Col + g.Buttons.Min(b => b.Col)) * Cell;
                float ly = Pad + (g.Row + g.Buttons.Min(b => b.Row)) * Cell;
                Text(bar, g.Label, lx, ly, 400, LabelHeight - 4, 21, TextAlignmentOptions.BottomLeft, LabelColour);
            }
            foreach (var b in g.Buttons)
            {
                var (x, y) = CellPixel(b);
                var colour = VrHotkeys.IsDown(b) ? DownColour : ButtonColour;
                var img = Box(bar, x, y, ButtonSize, ButtonSize, colour);
                if (VrUiMenu.RoundedSprite() is Sprite r) { img.sprite = r; img.type = Image.Type.Sliced; }
                // The game's sprite in its own colours, else the icon in cream, else the label.
                var art = b.GameSprite();
                Image picture = null;
                if (art != null) picture = Picture(bar, art, x, y, ArtSize, Color.white);
                else if (!(b.Icon != null && VrIcons.Get(b.Icon) is Sprite icon && Picture(bar, icon, x, y, IconSize, TextColour) != null))
                    Text(bar, b.Shown, x, y + 4, ButtonSize, ButtonSize - 8, b.Shown.Length > 3 ? 16 : 22, TextAlignmentOptions.Center, TextColour);
                bar.Controls.Add(new Control { Rect = new Rect(x, y, ButtonSize, ButtonSize), Image = img, Color = colour, Button = b, Art = art, Picture = picture });
            }
        }
    }

    static RectTransform Place(Bar bar, float x, float y, float w, float h)
    {
        var go = new GameObject("Item") { layer = Layer };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(bar.Content, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    static Image Box(Bar bar, float x, float y, float w, float h, Color colour)
    {
        var img = Place(bar, x, y, w, h).gameObject.AddComponent<Image>();
        var mat = VrFont.ImageMaterial(VrUi.Host);
        if (mat != null) img.material = mat;
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    // A sprite centred on the button at (x, y), fitted in size x size with its aspect kept.
    static Image Picture(Bar bar, Sprite sprite, float x, float y, float size, Color colour)
    {
        float inset = (ButtonSize - size) / 2;
        var img = Box(bar, x + inset, y + inset, size, size, colour);
        img.sprite = sprite;
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        return img;
    }

    // A few times a second (and at once after VrHotkeys.Refresh): ask the live buttons for
    // their art and label. One game sprite swapped for another is changed in place; art
    // coming or going, or new text on a button that shows text, redraws the bar.
    static void LookAtLive(Bar bar)
    {
        float now = Time.unscaledTime;
        if (now < bar.NextArtLook) return;
        bar.NextArtLook = now + ArtLook;
        if (bar.Dirty || bar.Controls.Count == 0)
        {
            // About to draw: start with what the game shows now.
            foreach (var g in bar.Set.Groups)
                foreach (var b in g.Buttons)
                    if (b.IsLive) b.Look();
            return;
        }
        foreach (var c in bar.Controls)
        {
            if (!c.Button.IsLive || !c.Button.Look()) continue;
            var sprite = c.Button.GameSprite();
            if (sprite != null && c.Picture != null && c.Art != null) { c.Picture.sprite = sprite; c.Art = sprite; }
            else bar.Dirty = true;
        }
    }

    static void Text(Bar bar, string text, float x, float y, float w, float h, float size, TextAlignmentOptions align, Color colour)
    {
        VrFont.MakeText(Place(bar, x, y, w, h).gameObject, font, text, size, align, colour, false);
    }

    // For scripted tests: tracking-space centre of the button with this label, on any bar showing.
    public static NVec? PointOf(string label)
    {
        foreach (var bar in bars)
        {
            if (!bar.Visible) continue;
            var c = bar.Controls.FirstOrDefault(k => k.Button.Label == label) ?? bar.Controls.FirstOrDefault(k => k.Button.Shown == label);
            if (c == null) continue;
            float x = c.Rect.center.x, y = c.Rect.center.y;
            return bar.TrackingPose.Compose(new Pose(new NVec((x / bar.PixelWidth - 0.5f) * bar.Width, (0.5f - y / bar.PixelHeight) * bar.Height, 0), NQuat.Identity)).Position;
        }
        return null;
    }
}
