#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// What a piece of the game's UI is, which decides how it can be shown.
enum UiPieceKind
{
    Screen, // a block of HUD laid out on the screen (resources, build bar...)
    World,  // drawn where something in the world is on the game's screen (markers, pointers to targets, bubbles)
    Mouse,  // follows the game's mouse (cursor hints, hover info)
}

// A piece of the game's UI shown on its own in VR. The game names the UI
// objects that belong to it; the window shows whatever they currently draw,
// cut out of the one UI texture (nothing is rendered twice). A "remainder"
// window shows everything no other window claims (pop-ups, inspectors, the
// pause menu), and hides when that is nothing.
//
// World and Mouse pieces are scattered over the screen and move every frame, so
// their window is "sparse": a screen-sized frame showing only the bits of the
// texture their graphics cover, re-cut each frame. Without a place in the
// layout they sit where the game's UI panel is.
sealed class UiWindowDef
{
    public string Id, Title;
    // The UI objects the window shows; null entries (not open right now) are skipped.
    public Func<IEnumerable<Transform>> Roots;
    public bool Remainder;
    public UiPieceKind Kind;
    // Screen pieces: measure the window from the roots' own rects instead of
    // every graphic under them (for roots whose children spill out, e.g. glows).
    public bool OwnRect;
    // World pieces: the world point an element (a child of a root) belongs to,
    // or null to find it back through the game camera (VrPieces.GameCamera).
    public Func<Transform, Vector3?> Anchor;
    // A window the game opens from another (ATS: the build list from the build
    // bar): with no place of its own in the layout, it goes beside its opener.
    public string OpensFrom;
    public OpenSide OpensSide = OpenSide.Right;
    // The point that stays put when the window changes size, for placements that don't
    // say one (no pivot= in the layout): TopLeft for a list that grows down, Bottom for a
    // bar that grows up. The player can change it while arranging (the dots on its outline).
    public UiPin Pin = UiPin.Centre;
    // Always cut from the one UI picture, even when windows pop out (a window
    // that doesn't work popped out in this game).
    public bool Cutout;
    public bool Sparse => Kind != UiPieceKind.Screen && !Remainder;
}

enum OpenSide { Right, Left, Above, Below }

sealed class UiWindow
{
    public readonly UiWindowDef Def;
    public Rect Source;              // UI-texture pixels, y up; zero size = nothing drawn
    public readonly List<Rect> Parts = new(); // sparse windows: the bits drawn (texture pixels)
    public readonly List<Transform> Roots = new(); // their roots, as last found (sparse and Separate windows use them)
    // Separate mode: the texture this window's roots are drawn into on their own
    // (screen-sized, the roots at their screen place), instead of the shared one.
    public RenderTexture Own;
    // The rect the window is shown at: Source, or while editing an empty window,
    // a placeholder of its last size so it can still be seen and moved.
    public Rect Shown { get; private set; }
    public bool Empty => Source.width <= 1 || Source.height <= 1;
    public bool Visible => (frame != null && frame.activeSelf) || (quad != null && quad.activeSelf)
        || (partQuads.Count > 0 && partQuads[0].go.activeSelf);

    // Editing (Arranged, VR menu open): the pink outline round the window, brighter when
    // the laser is on it or it is being moved, yellow when letting go would put it on a wrist.
    public enum Highlight { None, Idle, Hover, Held, Snap }

    // Where the window was last shown, in tracking space (metres): its centre and size.
    public Pose TrackingCentre { get; private set; }
    public float TrackingWidth { get; private set; }
    public float TrackingHeight { get; private set; }

    // Distance (metres) from a tracking-space point to the window's rectangle.
    public float DistanceTo(NVec point)
    {
        if (!Visible) return float.MaxValue;
        var local = NVec.Transform(point - TrackingCentre.Position, NQuat.Inverse(TrackingCentre.Rotation));
        float dx = MathF.Max(0, MathF.Abs(local.X) - TrackingWidth * 0.5f);
        float dy = MathF.Max(0, MathF.Abs(local.Y) - TrackingHeight * 0.5f);
        return MathF.Sqrt(dx * dx + dy * dy + local.Z * local.Z);
    }

    GameObject quad, frame, fill;
    readonly GameObject[] edges = new GameObject[4];
    Material material, frameMaterial;
    Pose worldPose;
    float worldWidth, worldHeight;
    Vector2 lastSize = new(480, 320);

    // The shared outline's look (UiOutline): thin, pink, yellow to snap.
    const float FrameBorder = UiOutline.Border; // metres, tracking space
    static readonly Color FrameIdle = UiOutline.Idle;
    static readonly Color FrameHover = UiOutline.Hover;
    static readonly Color FrameHeld = UiOutline.Held;
    static readonly Color FrameSnap = UiOutline.Snap;
    static readonly Color FrameFill = UiOutline.Fill;

    public UiWindow(UiWindowDef def) => Def = def;

    public void Place(IVrUiHost host, Pose trackingAnchor, UiPlacement p, Highlight highlight)
    {
        var tex = host.UiTexture;
        bool editing = highlight != Highlight.None;
        if (!Empty) lastSize = Source.size;
        if (tex == null || (p.Hidden && !editing) || (Empty && !editing))
        {
            Hide();
            return;
        }
        Ensure(host);
        Floating = false;
        Shown = Empty ? new Rect((Screen.width - lastSize.x) * 0.5f, (Screen.height - lastSize.y) * 0.5f, lastSize.x, lastSize.y) : Source;

        float w = Shown.width / 1000f * p.Size, h = Shown.height / 1000f * p.Size;
        var centre = p.CentreIn(trackingAnchor, w, h);
        Pin = p.Pin;
        TrackingCentre = centre;
        TrackingWidth = w;
        TrackingHeight = h;
        worldPose = host.TrackingToWorld(centre);
        worldWidth = w * host.WorldScale;
        worldHeight = h * host.WorldScale;

        quad.SetActive(!Empty && !Def.Sparse);
        if (Def.Sparse) PlaceParts(host, tex, p.Size);
        else if (!Empty)
        {
            // Without what pieces in the world / at the laser draw (it would show twice);
            // in Separate mode, the window's own texture (only its roots in it).
            Texture shown = Own != null ? Own : VrPieces.Shown(tex);
            if (material.mainTexture != shown)
            {
                material.mainTexture = shown;
                material.SetTexture("_BaseMap", shown);
            }
            var offset = new Vector2(Source.x / tex.width, Source.y / tex.height);
            var scale = new Vector2(Source.width / tex.width, Source.height / tex.height);
            material.mainTextureOffset = offset;
            material.mainTextureScale = scale;
            material.SetTextureOffset("_BaseMap", offset);
            material.SetTextureScale("_BaseMap", scale);
            quad.transform.SetPositionAndRotation(worldPose.UnityPosition, worldPose.UnityRotation);
            quad.transform.localScale = new Vector3(worldWidth, worldHeight, 1);
        }

        frame.SetActive(editing);
        if (editing)
        {
            var colour = highlight switch
            {
                Highlight.Held => FrameHeld,
                Highlight.Snap => FrameSnap,
                Highlight.Hover => FrameHover,
                _ => FrameIdle,
            };
            frameMaterial.color = colour;
            frameMaterial.SetColor("_BaseColor", colour);
            float b = FrameBorder * host.WorldScale, hw = worldWidth * 0.5f, hh = worldHeight * 0.5f;
            frame.transform.SetPositionAndRotation(worldPose.UnityPosition, worldPose.UnityRotation);
            SetLocal(edges[0], 0, hh + b * 0.5f, worldWidth + 2 * b, b);
            SetLocal(edges[1], 0, -hh - b * 0.5f, worldWidth + 2 * b, b);
            SetLocal(edges[2], -hw - b * 0.5f, 0, b, worldHeight);
            SetLocal(edges[3], hw + b * 0.5f, 0, b, worldHeight);
            fill.SetActive(Empty);
            if (Empty) SetLocal(fill, 0, 0, worldWidth, worldHeight);
        }
        metresPerPixel = p.Size / 1000f;
        ShowPins(host, editing && ShowPinDots);
    }

    // ---- the pin: the point that stays put when the window changes size ----
    //
    // While arranging, the window under the laser (or held) shows its nine pin points
    // as dots on its outline: the current one solid and larger, the one under the laser
    // brighter. The trigger on a dot pins the window there (VrUi.SetPin) without moving it.

    public UiPin Pin { get; private set; } = UiPin.Centre;
    public UiPin? HoverPin;      // set by VrUi.Raycast: the dot under the laser
    public bool ShowPinDots;     // set by VrUi before Place: hovered or held while arranging
    float metresPerPixel = 0.0008f;

    const float DotSize = 0.008f, PinnedDotSize = 0.014f; // tracking metres
    const float PinReach = 0.018f;                        // how near the laser must be to a dot
    readonly GameObject[] dots = new GameObject[9];
    Material dotMaterial, pinnedMaterial, dotHoverMaterial;

    // The pin whose dot is at a pixel of Shown (y up), if any is within reach.
    public UiPin? PinAt(Vector2 pixel)
    {
        if (Shown.width < 1 || metresPerPixel <= 0) return null;
        UiPin? best = null;
        float bestD = PinReach;
        for (int i = 0; i < 9; i++)
        {
            var (x, y) = ((UiPin)i).Point();
            float dx = (Shown.x + x * Shown.width - pixel.x) * metresPerPixel, dy = (Shown.y + y * Shown.height - pixel.y) * metresPerPixel;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestD) { bestD = d; best = (UiPin)i; }
        }
        return best;
    }

    void ShowPins(IVrUiHost host, bool show)
    {
        if (!show)
        {
            if (dots[0] != null && dots[0].activeSelf) foreach (var d in dots) d.SetActive(false);
            return;
        }
        if (dots[0] == null)
        {
            dotMaterial = DotMaterial(host, UiOutline.Hover);
            pinnedMaterial = DotMaterial(host, UiOutline.Held);
            dotHoverMaterial = DotMaterial(host, Color.white);
            for (int i = 0; i < 9; i++) dots[i] = FramePart("Pin " + (UiPin)i, dotMaterial);
        }
        float s = host.WorldScale, z = -0.002f * s; // a little in front of the window, towards the viewer
        for (int i = 0; i < 9; i++)
        {
            var pin = (UiPin)i;
            var (x, y) = pin.Point();
            bool pinned = pin == Pin;
            float size = (pinned ? PinnedDotSize : DotSize) * s;
            var go = dots[i];
            if (!go.activeSelf) go.SetActive(true);
            go.transform.localPosition = new Vector3((x - 0.5f) * worldWidth, (y - 0.5f) * worldHeight, z);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(size, size, 1);
            var mat = HoverPin == pin ? dotHoverMaterial : pinned ? pinnedMaterial : dotMaterial;
            var r = go.GetComponent<Renderer>();
            if (r.sharedMaterial != mat) r.sharedMaterial = mat;
        }
    }

    static Material DotMaterial(IVrUiHost host, Color colour)
    {
        // Round where the rounded shapes can be made (a circle sprite's whole texture), else square.
        Texture tex = VrShapes.Circle(32) is Sprite c && c != null ? c.texture : Texture2D.whiteTexture;
        var m = host.MakeTexturedMaterial(tex);
        m.color = colour;
        m.SetColor("_BaseColor", colour);
        m.renderQueue = Dvr.VrDrawOrder.Panels + 1; // over the window and its outline
        return m;
    }

    static void SetLocal(GameObject go, float x, float y, float w, float h)
    {
        go.transform.localPosition = new Vector3(x, y, 0);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(w, h, 1);
    }

    public void Hide()
    {
        HideLabel();
        if (quad != null) quad.SetActive(false);
        if (frame != null) frame.SetActive(false);
        HideParts(0);
    }

    // ---- the window's name, just above its top-left corner (Free / Separate) ----

    GameObject label;
    Material labelMaterial;

    // After Place: the window's row of the names texture (VrLabels), on a small
    // quad above the window, facing the same way, sized with the window.
    public void ShowLabel(IVrUiHost host, Texture atlas, Rect uv, float aspect)
    {
        if (quad == null || TrackingWidth <= 0 || !Visible) { HideLabel(); return; }
        if (label == null)
        {
            label = UiGeometry.MakeQuad("VrUi Name " + Def.Id, host.VisualLayer);
            labelMaterial = host.MakeTexturedMaterial(atlas);
            labelMaterial.renderQueue = Dvr.VrDrawOrder.Panels + 1; // over its window's edge
            label.GetComponent<Renderer>().sharedMaterial = labelMaterial;
        }
        if (labelMaterial.mainTexture != atlas) { labelMaterial.mainTexture = atlas; labelMaterial.SetTexture("_BaseMap", atlas); }
        labelMaterial.mainTextureOffset = uv.position;
        labelMaterial.mainTextureScale = uv.size;
        labelMaterial.SetTextureOffset("_BaseMap", uv.position);
        labelMaterial.SetTextureScale("_BaseMap", uv.size);
        // Readable at arm's length, growing with the window within limits (tracking metres).
        float h = Math.Clamp(TrackingHeight * 0.12f, 0.022f, 0.05f), w = h * aspect;
        var pose = TrackingCentre.Compose(new Pose(new NVec(-TrackingWidth * 0.5f + w * 0.5f, TrackingHeight * 0.5f + h * 0.5f + 0.004f, 0), NQuat.Identity));
        var world = host.TrackingToWorld(pose);
        if (!label.activeSelf) label.SetActive(true);
        label.transform.SetPositionAndRotation(world.UnityPosition, world.UnityRotation);
        label.transform.localScale = new Vector3(w * host.WorldScale, h * host.WorldScale, 1);
    }

    public void HideLabel() { if (label != null && label.activeSelf) label.SetActive(false); }

    // ---- sparse windows: one quad per part, at its place within the window ----

    const int MaxParts = 24;
    readonly List<(GameObject go, Material mat)> partQuads = new();

    void PlaceParts(IVrUiHost host, RenderTexture tex, float size)
    {
        int n = Math.Min(Parts.Count, MaxParts);
        for (int i = 0; i < n; i++)
        {
            var r = Parts[i];
            var local = new NVec((r.center.x - Shown.center.x) / 1000f * size, (r.center.y - Shown.center.y) / 1000f * size, 0);
            ShowPart(host, tex, i, r, TrackingCentre.Compose(new Pose(local, NQuat.Identity)), size / 1000f);
        }
        HideParts(n);
    }

    // Part i: texture rect r, centred at a tracking-space pose, metres per texture pixel.
    public void ShowPart(IVrUiHost host, RenderTexture tex, int i, Rect r, Pose at, float metresPerPixel)
    {
        if (i >= MaxParts) return;
        while (partQuads.Count <= i)
        {
            var go = UiGeometry.MakeQuad($"VrUi Part {Def.Id} {partQuads.Count}", host.VisualLayer);
            var mat = host.MakeTexturedMaterial(tex);
            mat.renderQueue = Dvr.VrDrawOrder.Panels;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            partQuads.Add((go, mat));
        }
        var (q, m) = partQuads[i];
        if (m.mainTexture != tex) { m.mainTexture = tex; m.SetTexture("_BaseMap", tex); }
        var offset = new Vector2(r.x / tex.width, r.y / tex.height);
        var scale = new Vector2(r.width / tex.width, r.height / tex.height);
        m.mainTextureOffset = offset;
        m.mainTextureScale = scale;
        m.SetTextureOffset("_BaseMap", offset);
        m.SetTextureScale("_BaseMap", scale);
        var world = host.TrackingToWorld(at);
        if (!q.activeSelf) q.SetActive(true);
        q.transform.SetPositionAndRotation(world.UnityPosition, world.UnityRotation);
        q.transform.localScale = new Vector3(r.width * metresPerPixel * host.WorldScale, r.height * metresPerPixel * host.WorldScale, 1);
    }

    // Shown away from the window frame (at the laser, in the world): the frame
    // and its quad are off, only the parts draw, and the laser goes through.
    public bool Floating;
    public void ShowFloating() { Floating = true; HideLabel(); if (quad != null) quad.SetActive(false); if (frame != null) frame.SetActive(false); }

    public void HideParts(int from)
    {
        for (int i = from; i < partQuads.Count; i++)
            if (partQuads[i].go.activeSelf) partQuads[i].go.SetActive(false);
    }

    // Where a world ray hits the window, as a pixel of Shown (y up). A sparse
    // window is only solid where it draws something.
    public bool Raycast(NVec origin, NVec dir, out Vector2 pixel, out float distance)
    {
        pixel = default;
        distance = 0;
        if (Floating || !Visible || !UiGeometry.QuadHit(worldPose, worldWidth, worldHeight, origin, dir, out float u, out float v, out distance)) return false;
        pixel = new Vector2(Shown.x + u * Shown.width, Shown.y + v * Shown.height);
        if (Def.Sparse && !(frame != null && frame.activeSelf))
        {
            var px = pixel;
            return Parts.Exists(r => r.Contains(px));
        }
        return true;
    }

    void Ensure(IVrUiHost host)
    {
        if (quad != null) return;
        quad = UiGeometry.MakeQuad("VrUi Window " + Def.Id, host.VisualLayer);
        // The game makes the material (its shader); the core decides when it draws.
        material = host.MakeTexturedMaterial(host.UiTexture);
        material.renderQueue = Dvr.VrDrawOrder.Panels;
        quad.GetComponent<Renderer>().sharedMaterial = material;
        // The frame: an outline of four thin quads, plus a faint fill that marks
        // out an empty window.
        frame = new GameObject("VrUi Frame " + Def.Id) { layer = host.VisualLayer };
        UiGeometry.Keep(frame);
        frameMaterial = host.MakeTexturedMaterial(Texture2D.whiteTexture);
        frameMaterial.renderQueue = Dvr.VrDrawOrder.Panels;
        var fillMaterial = host.MakeTexturedMaterial(Texture2D.whiteTexture);
        fillMaterial.color = FrameFill;
        fillMaterial.SetColor("_BaseColor", FrameFill);
        fillMaterial.renderQueue = material.renderQueue - 1; // behind the window
        for (int i = 0; i < edges.Length; i++) edges[i] = FramePart("Edge", frameMaterial);
        fill = FramePart("Fill", fillMaterial);
    }

    GameObject FramePart(string name, Material mat)
    {
        var go = UiGeometry.MakeQuad(name, frame.layer);
        go.transform.SetParent(frame.transform, false);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }
}

static class UiGeometry
{
    // Kept across scene loads. Only in play mode: the editor (UnityTests) refuses it outside.
    internal static void Keep(UnityEngine.Object o) { if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(o); }

    public static GameObject MakeQuad(string name, int layer)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        go.layer = layer;
        Keep(go);
        var collider = go.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.Destroy(collider);
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Register(go.GetComponent<Renderer>());
        return go;
    }

    // Our panels, menus, bars and labels, so a fitted eye far plane still takes
    // them in (CylinderCull.FitFar; it once cut the VR UI in tpm-vr).
    internal static readonly List<Renderer> Quads = new();
    public static void Register(Renderer r) { if (r != null) Quads.Add(r); }

    // Ray against a centred quad of the given world size; u, v in 0..1 from its bottom-left.
    public static bool QuadHit(Pose pose, float width, float height, NVec origin, NVec dir, out float u, out float v, out float distance)
    {
        u = v = distance = 0;
        var normal = NVec.Transform(NVec.UnitZ, pose.Rotation);
        float denom = NVec.Dot(dir, normal);
        if (MathF.Abs(denom) < 1e-5f) return false;
        distance = NVec.Dot(pose.Position - origin, normal) / denom;
        if (distance <= 0) return false;
        var local = NVec.Transform(origin + dir * distance - pose.Position, NQuat.Inverse(pose.Rotation));
        u = local.X / width + 0.5f;
        v = local.Y / height + 0.5f;
        return u >= 0 && u <= 1 && v >= 0 && v <= 1;
    }

    // Yaw and pitch (degrees) that turn a window at `position` to face `eye`.
    public static (float yaw, float pitch) Facing(NVec position, NVec eye)
    {
        var d = position - eye;
        float yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        float pitch = MathF.Atan2(-d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * 180f / MathF.PI;
        return (yaw, pitch);
    }

    // Yaw and pitch (degrees) of a rotation, from where it points +Z (roll is dropped).
    public static (float yaw, float pitch) YawPitch(NQuat rotation)
    {
        var f = NVec.Transform(NVec.UnitZ, rotation);
        return Facing(f, NVec.Zero);
    }
}

// Screen rects of what uGUI objects actually draw: the union of their visible
// graphics, skipping invisible ones and full-screen backdrops.
static class UiBounds
{
    const float Pad = 6f;
    // Games often draw a frame around the screen edge (e.g. a paused-game border);
    // windows touching an edge stop this far short of it.
    const float EdgeInset = 9f;

    public static Rect Of(Camera cam, IEnumerable<Transform> roots)
    {
        var acc = new Acc();
        foreach (var root in roots)
        {
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            foreach (var g in root.GetComponentsInChildren<Graphic>()) acc.Add(cam, g);
        }
        return acc.Result();
    }

    // As above, leaving out graphics under another piece's roots (skip) or excluded
    // names below each root (a tooltip that lives inside a HUD block, say).
    public static Rect Of(Camera cam, IEnumerable<Transform> roots, HashSet<long> skip, HashSet<string> skipNames)
    {
        var acc = new Acc();
        foreach (var root in roots)
        {
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            foreach (var g in root.GetComponentsInChildren<Graphic>())
            {
                bool skipped = false;
                for (var t = g.transform; t != null && t != root; t = t.parent)
                    if (skip.Contains(VrCore.Key(t)) || NameMatches(t.name, skipNames)) { skipped = true; break; }
                if (!skipped) acc.Add(cam, g);
            }
        }
        return acc.Result();
    }

    // The roots' own rects (not what's drawn under them), clamped to the screen.
    public static Rect OfOwnRects(Camera cam, IEnumerable<Transform> roots)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var root in roots)
        {
            if (root == null || !root.gameObject.activeInHierarchy || root is not RectTransform rt) continue;
            var r = rt.rect;
            var a = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0)));
            var b = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0)));
            x0 = Math.Min(x0, Math.Min(a.x, b.x)); x1 = Math.Max(x1, Math.Max(a.x, b.x));
            y0 = Math.Min(y0, Math.Min(a.y, b.y)); y1 = Math.Max(y1, Math.Max(a.y, b.y));
        }
        if (x1 <= x0 || y1 <= y0) return default;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Screen.width, x1); y1 = Math.Min(Screen.height, y1);
        return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : default;
    }

    // Each visible graphic's rect under the roots, padded, with overlapping ones
    // merged (a sparse window's parts).
    public static void Parts(Camera cam, IEnumerable<Transform> roots, List<Rect> parts)
    {
        parts.Clear();
        foreach (var root in roots)
        {
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            foreach (var g in root.GetComponentsInChildren<Graphic>())
            {
                var acc = new Acc();
                if (!acc.Add(cam, g, out var r)) continue;
                r = Rect.MinMaxRect(Math.Max(0, r.xMin - Pad), Math.Max(0, r.yMin - Pad),
                    Math.Min(Screen.width, r.xMax + Pad), Math.Min(Screen.height, r.yMax + Pad));
                // Merge with whatever it touches, repeatedly (a merge can reach further ones).
                for (int i = parts.Count - 1; i >= 0; i--)
                    if (parts[i].Overlaps(r))
                    {
                        r = Rect.MinMaxRect(Math.Min(r.xMin, parts[i].xMin), Math.Min(r.yMin, parts[i].yMin),
                            Math.Max(r.xMax, parts[i].xMax), Math.Max(r.yMax, parts[i].yMax));
                        parts.RemoveAt(i);
                        i = parts.Count;
                    }
                parts.Add(r);
            }
        }
    }

    // Everything drawn on the canvases except under the claimed / excluded objects.
    // trace: if given, gets a line for each graphic that counted (for diagnosing stray content).
    public static Rect Remainder(Camera cam, IReadOnlyList<Canvas> canvases, HashSet<long> skip, HashSet<string> skipNames, List<string> trace = null)
    {
        var acc = new Acc();
        foreach (var canvas in canvases)
        {
            if (canvas == null || !canvas.isActiveAndEnabled || NameMatches(canvas.name, skipNames)) continue;
            foreach (var g in canvas.GetComponentsInChildren<Graphic>())
            {
                if (Skipped(g.transform, skip, skipNames)) continue;
                if (acc.Add(cam, g, out var r) && trace != null) trace.Add($"{Path(g.transform)} {r}");
            }
        }
        return acc.Result();
    }

    static string Path(Transform t)
    {
        var s = t.name;
        for (t = t.parent; t != null; t = t.parent) s = t.name + "/" + s;
        return s;
    }

    // Exact names, or a prefix ending in "*" (for "(Clone)"s and variants).
    internal static bool NameMatches(string name, HashSet<string> names)
    {
        if (names.Contains(name)) return true;
        foreach (var n in names)
            if (n.EndsWith("*") && name.StartsWith(n[..^1], StringComparison.Ordinal)) return true;
        return false;
    }

    static bool Skipped(Transform t, HashSet<long> skip, HashSet<string> skipNames)
    {
        for (; t != null; t = t.parent)
            if (skip.Contains(VrCore.Key(t)) || NameMatches(t.name, skipNames)) return true;
        return false;
    }

    struct Acc
    {
        float x0, y0, x1, y1;
        int n;

        public bool Add(Camera cam, Graphic g) => Add(cam, g, out _);

        public bool Add(Camera cam, Graphic g, out Rect added)
        {
            added = default;
            if (!g.isActiveAndEnabled || g.color.a < 0.01f) return false;
            var cr = g.canvasRenderer;
            if (cr == null || cr.cull || cr.GetInheritedAlpha() < 0.01f) return false;
            var rt = g.rectTransform;
            var r = rt.rect;
            var a = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0)));
            var b = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0)));
            float ax = Math.Min(a.x, b.x), bx = Math.Max(a.x, b.x), ay = Math.Min(a.y, b.y), by = Math.Max(a.y, b.y);
            if (bx - ax >= Screen.width * 0.98f && by - ay >= Screen.height * 0.98f) return false; // full-screen backdrop
            if (bx <= 0 || by <= 0 || ax >= Screen.width || ay >= Screen.height) return false; // off screen
            added = new Rect(ax, ay, bx - ax, by - ay);
            if (n++ == 0) { x0 = ax; y0 = ay; x1 = bx; y1 = by; return true; }
            x0 = Math.Min(x0, ax); y0 = Math.Min(y0, ay); x1 = Math.Max(x1, bx); y1 = Math.Max(y1, by);
            return true;
        }

        public Rect Result()
        {
            if (n == 0) return default;
            float ax = Math.Max(EdgeInset, x0 - Pad), ay = Math.Max(EdgeInset, y0 - Pad);
            float bx = Math.Min(Screen.width - EdgeInset, x1 + Pad), by = Math.Min(Screen.height - EdgeInset, y1 + Pad);
            return new Rect(ax, ay, bx - ax, by - ay);
        }
    }
}
