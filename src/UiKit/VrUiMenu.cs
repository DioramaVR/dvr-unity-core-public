#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// Our own VR menu: view modes, the game's UI, controls, capture and settings. It
// is drawn with stock uGUI pieces (Image, TextMeshPro) on a canvas that only our
// menu camera renders, into a texture shown on a quad. Clicks are ours too: the
// laser hit is tested against the controls' rectangles, so no uGUI events or
// injected classes are involved (Denuvo rules out class injection in some games).
//
// This file: the camera, texture, quad, laser hits, carrying and the drawing
// helpers. What's drawn (the redesign, docs/menu-design): Menu/VrUiMenu.New.cs,
// from MenuModel / MenuState. The first, tabbed 800 x 800 menu was removed on
// 2026-11-06 once the new one had been tried in a headset.
static partial class VrUiMenu
{
    const int W = 1200, H = 800;
    const int Layer = 5; // UI: the eye cameras never render it
    static readonly Vector3 CameraPosition = new(0, -120, 0);
    const float WidthMetres = 0.42f * W / 640; // the first menu's pixels per metre

    static Color Hex(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    static readonly Color Back = Hex(0x181a25);
    static readonly Color TextColor = Hex(0xf5f0e8);

    static Camera cam;
    static RenderTexture texture;
    internal static RenderTexture Texture => texture;   // VrSelfTest
    static Canvas canvas;
    static Transform content;
    static GameObject quad;
    static Material material;
    static TMP_FontAsset font;
    static Pose trackingPose;
    static Pose worldPose;
    static bool dirty = true;

    sealed class Control
    {
        public string Label; // for scripted tests (CentreOf)
        public Rect Rect;  // canvas pixels from the top-left
        public Image Image;
        public Color Color;
        public Action Click;
        public bool InScroll; // the new menu: in the scrolled area (Rect at scroll 0)
    }
    static readonly List<Control> controls = new();
    static Control hovered;

    public static bool Open { get; private set; }

    // The grip carries the menu (VrUi.CarryPanels); once moved it reopens there.
    static bool moved;
    static float scale = 1;
    static float Width => WidthMetres * scale;
    static float Height => WidthMetres * scale * H / W;

    public static void Toggle(IVrUiHost host)
    {
        if (Open) Close();
        else Show(host);
    }

    // Opens in front of the user, below eye level, facing them.
    public static void Show(IVrUiHost host)
    {
        var head = host.Head;
        var f = head.Forward;
        var flat = new NVec(f.X, 0, f.Z);
        flat = flat.LengthSquared() < 1e-4f ? NVec.UnitZ : NVec.Normalize(flat);
        var pos = head.Position + flat * 0.5f + new NVec(0, -0.22f, 0);
        if (!moved) trackingPose = HandDrag.FacingHead(host, pos);
        Open = true;
        dirty = true;
        host.Log("[vrui] menu opened");
    }

    public static void Close()
    {
        Open = false;
        hovered = null;
        if (quad != null) quad.SetActive(false);
        if (cam != null) cam.enabled = false;
    }

    public static void MarkDirty() => dirty = true;

    // Leave the menu out of one camera's picture (e.g. a recording view).
    public static void SetRendered(bool on)
    {
        if (quad == null) return;
        var r = quad.GetComponent<Renderer>();
        if (r != null && r.enabled != on) r.enabled = on;
    }

    static bool loggedInactive, loggedDrawn;
    static string lastError;

    public static void Update(IVrUiHost host)
    {
        if (!Open || !host.Active)
        {
            if (Open && !loggedInactive) { loggedInactive = true; host.Log("[vrui] menu is open but not drawn: the mod says VR isn't active (IVrUiHost.Active)"); }
            if (quad != null) quad.SetActive(false);
            if (cam != null) cam.enabled = false;
            return;
        }
        try
        {
            if (!Ensure(host)) return;
            if (dirty) Rebuild(host);
            TickNew();
        }
        catch (Exception e)
        {
            // Not every frame: log it once and close, so the chord can try again.
            if (e.Message != lastError) { lastError = e.Message; host.Log($"[vrui] menu failed to draw, closed: {e}"); }
            Close();
            return;
        }
        if (!loggedDrawn)
        {
            loggedDrawn = true;
            var shader = material != null && material.shader != null ? material.shader.name : "none";
            host.Log($"[vrui] menu drawn on layer {quad.layer} with shader '{shader}' (if it isn't seen, check the eye cameras draw layer {quad.layer})");
        }
        cam.enabled = true;
        float w = Width, h = Height;
        worldPose = host.TrackingToWorld(trackingPose);
        quad.SetActive(true);
        quad.transform.SetPositionAndRotation(worldPose.UnityPosition, worldPose.UnityRotation);
        quad.transform.localScale = new Vector3(w * host.WorldScale, h * host.WorldScale, 1);
    }

    public static bool Raycast(IVrUiHost host, NVec origin, NVec dir, out float distance)
    {
        distance = 0;
        Control hit = null;
        float u = 0, v = 0;
        bool onMenu = Open && quad != null && quad.activeSelf &&
            UiGeometry.QuadHit(worldPose, Width * host.WorldScale, Height * host.WorldScale, origin, dir, out u, out v, out distance);
        if (onMenu)
        {
            var p = new Vector2(u * W, (1 - v) * H);
            hit = controls.LastOrDefault(c => c.Click != null && Hits(c, p));
            laserPoint = p;
        }
        else laserPoint = null;
        if (hit != hovered)
        {
            if (hovered != null && hovered.Image != null) hovered.Image.color = hovered.Color;
            hovered = hit;
            if (hovered != null) hovered.Image.color = Color.Lerp(hovered.Color, Color.white, 0.18f);
        }
        return onMenu;
    }

    // Tracking-space point at canvas pixel (x, y from the top-left).
    public static NVec PointToTracking(float x, float y)
    {
        float w = Width, h = Height;
        return trackingPose.Compose(new Pose(new NVec((x / W - 0.5f) * w, (0.5f - y / H) * h, 0), NQuat.Identity)).Position;
    }

    // Canvas pixel centre of the clickable control with this label, as last drawn.
    public static bool CentreOf(string label, out float x, out float y)
    {
        // The first menu's tab names, which games' scripted tests still use.
        string alias = label switch { "Welcome" => "Home", "Mode" => "View", "UI" => "Game UI", "Record" => "Capture", _ => null };
        var c = controls.FirstOrDefault(k => k.Click != null && (k.Label == label || (alias != null && k.Label == alias)));
        x = y = 0;
        if (c == null) return false;
        var r = c.Rect;
        // Scrolled: bring it into view first, then where it is now.
        if (c.InScroll)
        {
            if (!scroller.Visible(r) || !scroller.Hits(r, scroller.OnCanvas(r).center)) { scroller.ScrollIntoView(r); if (scrollRoot != null) scroller.Apply(scrollRoot); PlaceThumb(); }
            r = scroller.OnCanvas(r);
        }
        x = r.center.x;
        y = r.center.y;
        return true;
    }

    // ---- carried (VrUi.CarryPanels) ----

    internal static bool Shown => Open && quad != null && quad.activeSelf;
    internal static Pose CarryPose => trackingPose;
    internal static void CarryTo(Pose p) => trackingPose = p;
    internal static float DistanceTo(NVec point) => Shown ? HandDrag.DistanceTo(trackingPose, Width, Height, point) : float.MaxValue;
    internal static void Resize(float factor) => scale = Math.Clamp(scale * factor, 0.5f, 2f);
    internal static void EndCarry(IVrUiHost host)
    {
        moved = true;
        host.Log($"[vrui] menu moved to {trackingPose.Position}, size {scale:F2}");
    }

    // The laser's trigger went down while on the menu.
    public static void Click()
    {
        var c = hovered;
        if (c?.Click == null) return;
        c.Click();
        dirty = true;
    }

    static IVrUiHost logHost;

    static bool Ensure(IVrUiHost host)
    {
        logHost = host;
        if (font == null)
        {
            font = VrFont.Get(host);
            // No TMP font: UI Text (VrFont.UseLegacy), or wait; VrFont logs why.
            if (font == null && !VrFont.UseLegacy(host)) return false;
        }
        // The style changed: a new texture and canvas at the other size.
        if (texture != null && texture.width != W)
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
            if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject);
            canvas = null;
            if (material != null) material.mainTexture = null;
        }
        if (texture == null)
        {
            texture = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "VrUi Menu",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            texture.Create();
            if (cam != null) cam.targetTexture = texture;
            if (material != null)
            {
                material.mainTexture = texture;
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            }
        }
        if (cam == null)
        {
            var go = new GameObject("VrUi Menu Camera");
            UiGeometry.Keep(go);
            go.transform.position = CameraPosition;
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 5f;
            cam.depth = -19;
            cam.cullingMask = 1 << Layer;
            cam.targetTexture = texture;
        }
        host.PrepareCamera(cam);
        if (canvas == null)
        {
            var go = new GameObject("VrUi Menu Canvas");
            UiGeometry.Keep(go);
            go.layer = Layer;
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            Box(go.transform, "Back", 0, 0, W, H, Back);
            var holder = new GameObject("Content");
            holder.layer = Layer;
            // Adding a RectTransform replaces (destroys) the plain Transform, so keep the new one.
            var rt = holder.AddComponent<RectTransform>();
            content = rt;
            rt.SetParent(go.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
        if (quad == null)
        {
            quad = UiGeometry.MakeQuad("VrUi Menu", host.VisualLayer);
            material = host.MakeTexturedMaterial(texture);
            material.renderQueue = Dvr.VrDrawOrder.Menu; // over the UI windows
            // Opaque: the menu texture's alpha is only partly written by uGUI.
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            quad.GetComponent<Renderer>().sharedMaterial = material;
        }
        return true;
    }

    static void Rebuild(IVrUiHost host)
    {
        dirty = false;
        hovered = null;
        controls.Clear();
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            // Inactive at once: Destroy waits for the frame's end, and the new page is drawn now.
            var child = content.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
        BuildNew(host);
    }

    // The hotkey bars' buttons share the menu's rounded corners.
    internal static Sprite RoundedSprite() => VrShapes.Fill(VrShapes.Button);

    static RectTransform Place(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name);
        go.layer = Layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    static Image Box(Transform parent, string name, float x, float y, float w, float h, Color colour)
    {
        var img = Place(parent, name, x, y, w, h).gameObject.AddComponent<Image>();
        var mat = VrFont.ImageMaterial(VrUi.Host);
        if (mat != null) img.material = mat;
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    static void Text(Transform parent, string text, float x, float y, float w, float h, float size, TextAlignmentOptions align, Color? colour = null, bool wrap = false, bool heading = false, int weight = 0)
    {
        VrFont.MakeText(Place(parent, "Text", x, y, w, h).gameObject, font, text, size, align, colour ?? TextColor, wrap, heading, weight);
    }
}
