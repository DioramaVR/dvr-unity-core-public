#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// Where each named UI piece is shown: in its window (where the layout or Free
// puts it; in Panel mode, on the panel), beside the laser (Mouse pieces), in
// the world over what it belongs to (World pieces), or not at all. The laser
// and the world work in Panel mode too: their bits are cut out of the panel. Chosen per piece on
// the UI tab, kept in [VrUi] PiecePlace <id>.
static class VrPieces
{
    public enum Place { Auto, Window, AtLaser, World, Hidden }

    // ---- what the mod tells us ----

    // The game's camera: World pieces' screen spots are turned back into world points through it.
    public static Func<Camera> GameCamera;
    // A world point under a screen pixel of the game camera (y up), e.g. the
    // mod's own terrain raycast. Without it: the plane y = GroundHeight.
    public static Func<Vector2, Vector3?> ScreenToWorld;
    public static float GroundHeight;

    // Where the laser ends this frame (world space): Mouse pieces go beside it.
    public static void LaserEnd(Vector3 world) { laserEnd = world; laserFrame = Time.frameCount; }
    static Vector3 laserEnd;
    static int laserFrame = -1000;
    static bool LaserKnown => laserFrame >= Time.frameCount - 2;

    // Apparent height of a whole screen of UI, degrees, for pieces out in the
    // world or at the laser (as a monitor from a desk), times Size.
    const float ScreenAngle = 35f;
    static ConfigEntry<float> size;

    // ---- the choice per piece ----

    static ConfigFile config;
    static readonly Dictionary<string, ConfigEntry<Place>> places = new();

    internal static void Init(ConfigFile file)
    {
        config = file;
        size = file.Bind("VrUi", "PieceSize", 1f, "Size of UI pieces shown in the world or at the laser (1 = a screen of UI spans 35 degrees).");
    }

    // Once, the first frame (the game has registered its pieces by then): a row each on the UI tab.
    internal static void AddRows(IEnumerable<UiWindow> windows)
    {
        if (config == null) return;
        foreach (var w in windows)
        {
            var e = Entry(w.Def);
            var options = Options(w.Def.Kind);
            VrSettings.Add(new ChoiceSetting("UI pieces", w.Def.Title ?? w.Def.Id,
                () => options.Select(Name).ToList(),
                () => Name(e.Value),
                v => { e.Value = options.FirstOrDefault(o => Name(o) == v); VrUiMenu.MarkDirty(); })
            {
                // In Panel mode only the world / laser choice means anything.
                When = w.Def.Kind == UiPieceKind.Screen ? () => VrUi.Mode != VrUi.UiMode.Panel : null,
            });
        }
        VrSettings.Add(new NumberSetting("UI pieces", "Size in the world", size, 0.3f, 3f, 0.1f, "0.0"));
        VrSettings.PutOnTab("UI pieces", "UI");
    }

    static Place[] Options(UiPieceKind kind) => kind switch
    {
        UiPieceKind.Mouse => new[] { Place.Auto, Place.Window, Place.AtLaser, Place.Hidden },
        UiPieceKind.World => new[] { Place.Auto, Place.Window, Place.World, Place.Hidden },
        _ => new[] { Place.Window, Place.Hidden },
    };

    static string Name(Place p) => p switch
    {
        Place.Auto => "Auto",
        Place.Window => "Window",
        Place.AtLaser => "At the laser",
        Place.World => "In the world",
        _ => "Hidden",
    };

    static ConfigEntry<Place> Entry(UiWindowDef def)
    {
        if (!places.TryGetValue(def.Id, out var e))
            places[def.Id] = e = config.Bind("VrUi", "PiecePlace " + def.Id, def.Kind == UiPieceKind.Screen ? Place.Window : Place.Auto,
                $"Where '{def.Title ?? def.Id}' is shown in Arranged / Free: Auto, Window, AtLaser (mouse pieces), World (world pieces) or Hidden.");
        return e;
    }

    // Where a piece goes now; Auto picks the laser / the world when the mod supports it.
    public static Place PlaceOf(UiWindowDef def)
    {
        var p = config != null ? Entry(def).Value : Place.Auto;
        if (def.Kind == UiPieceKind.Screen) return p == Place.Hidden ? Place.Hidden : Place.Window;
        if (p == Place.Auto)
            p = def.Kind == UiPieceKind.Mouse ? (LaserKnown ? Place.AtLaser : Place.Window)
                : (def.Anchor != null || GameCamera != null ? Place.World : Place.Window);
        if (p == Place.AtLaser && (def.Kind != UiPieceKind.Mouse || !LaserKnown)) p = Place.Window;
        if (p == Place.World && def.Kind != UiPieceKind.World) p = Place.Window;
        return p;
    }

    // ---- cut out of the panel (Panel mode) ----

    // The texture rects of what was shown in the world / at the laser last frame.
    // They are cleared from the panel (Panel mode) and from every window's crop
    // (Arranged / Free), so they aren't shown twice: see Shown.
    public static List<Rect> PanelCuts => cuts;
    static List<Rect> cuts = new(), building = new();

    // The UI texture as the panel and the windows should show it: a copy with the
    // cut rects blanked (made once a frame, from the texture as last rendered), or
    // the texture itself when nothing is cut. Pieces out in the world / at the
    // laser draw from the live texture.
    public static Texture Shown(RenderTexture src)
    {
        if (src == null || cuts.Count == 0 || (int)SystemInfo.copyTextureSupport == 0) return src;
        if (cutFrame == Time.frameCount && cutCopy != null && cutCopy.width == src.width && cutCopy.height == src.height) return cutCopy;
        cutFrame = Time.frameCount;
        if (cutCopy == null || cutCopy.width != src.width || cutCopy.height != src.height)
        {
            if (cutCopy != null) { cutCopy.Release(); UnityEngine.Object.Destroy(cutCopy); }
            if (blank != null) { blank.Release(); UnityEngine.Object.Destroy(blank); }
            cutCopy = new RenderTexture(src.width, src.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "VrUi shown", hideFlags = HideFlags.DontUnloadUnusedAsset };
            blank = new RenderTexture(src.width, src.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "VrUi blank", hideFlags = HideFlags.DontUnloadUnusedAsset };
            cutCopy.Create();
            blank.Create();
            var was = RenderTexture.active;
            RenderTexture.active = blank;
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            RenderTexture.active = was;
        }
        Graphics.CopyTexture(src, 0, 0, cutCopy, 0, 0);
        foreach (var r in cuts)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(r.xMin), 0, src.width), y = Mathf.Clamp(Mathf.FloorToInt(r.yMin), 0, src.height);
            int w = Mathf.Clamp(Mathf.CeilToInt(r.xMax), 0, src.width) - x, h = Mathf.Clamp(Mathf.CeilToInt(r.yMax), 0, src.height) - y;
            if (w > 0 && h > 0) Graphics.CopyTexture(blank, 0, 0, x, y, w, h, cutCopy, 0, 0, x, y);
        }
        return cutCopy;
    }
    static RenderTexture cutCopy, blank;
    static int cutFrame = -1;

    internal static void BeginFrame() => building.Clear();
    internal static void EndFrame() => (cuts, building) = (building, cuts);
    // Hidden in Panel mode: off the panel too.
    internal static void CutOnly(UiWindow w) => building.AddRange(w.Parts);

    // ---- showing a piece out of its window ----

    // Beside the laser: the piece's bits together, just above where the laser ends.
    internal static bool ShowAtLaser(IVrUiHost h, UiWindow w)
    {
        var tex = h.UiTexture;
        if (tex == null || w.Parts.Count == 0) { w.Hide(); return true; }
        w.ShowFloating();
        var bounds = Union(w.Parts);
        var end = WorldToTracking(h, ToN(laserEnd));
        float mpp = MetresPerPixel(h, end);
        var centre = end + new NVec(0, bounds.height * 0.5f * mpp + 0.02f, 0);
        var facing = HandDrag.FacingHead(h, centre);
        int n = Math.Min(w.Parts.Count, 24);
        for (int i = 0; i < n; i++)
        {
            var r = w.Parts[i];
            var local = new NVec((r.center.x - bounds.center.x) * mpp, (r.center.y - bounds.center.y) * mpp, 0);
            w.ShowPart(h, tex, i, r, facing.Compose(new Pose(local, NQuat.Identity)), mpp);
            building.Add(r);
        }
        w.HideParts(n);
        return true;
    }

    // In the world: each element of the piece (a child of its roots) at the
    // world point it belongs to, from the piece's anchor hook or, failing that,
    // back through the game camera from where it is on the screen.
    internal static bool ShowInWorld(IVrUiHost h, UiWindow w)
    {
        var tex = h.UiTexture;
        if (tex == null) return false;
        w.ShowFloating();
        var cam = GameCamera?.Invoke();
        int n = 0;
        foreach (var root in w.Roots)
        {
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            foreach (var element in Elements(root))
            {
                if (n >= 24) break;
                var r = UiBounds.Of(h.UiCamera, new[] { element });
                if (r.width < 1 || r.height < 1) continue;
                Vector3? anchor = w.Def.Anchor?.Invoke(element);
                if (anchor == null && cam != null) anchor = ThroughCamera(cam, r.center);
                if (anchor == null) continue;
                var at = WorldToTracking(h, ToN(anchor.Value));
                // A little toward the viewer, so the thing it belongs to doesn't cut into it.
                var toHead = h.Head.Position - at;
                if (toHead.LengthSquared() > 1e-6f) at += NVec.Normalize(toHead) * Math.Min(0.03f, toHead.Length() * 0.5f);
                w.ShowPart(h, tex, n++, r, HandDrag.FacingHead(h, at), MetresPerPixel(h, at));
                building.Add(r);
            }
        }
        w.HideParts(n);
        return true;
    }

    // A piece's elements: its roots' active children, or the root itself when it draws alone.
    static IEnumerable<Transform> Elements(Transform root)
    {
        bool any = false;
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (!c.gameObject.activeInHierarchy) continue;
            any = true;
            yield return c;
        }
        if (!any) yield return root;
    }

    internal static Vector3? ThroughCamera(Camera cam, Vector2 pixel)
    {
        if (ScreenToWorld != null) return ScreenToWorld(pixel);
        // The UI texture is screen-sized, so its pixels are the game camera's.
        var ray = cam.ScreenPointToRay(new Vector3(pixel.x, pixel.y, 0));
        if (Mathf.Abs(ray.direction.y) < 1e-4f) return null;
        float t = (GroundHeight - ray.origin.y) / ray.direction.y;
        if (t <= 0) return null;
        Vector3 hit = ray.origin + ray.direction * t;
        return hit;
    }

    // Tracking metres per UI pixel at a point: a screen of UI spans ScreenAngle from the head.
    static float MetresPerPixel(IVrUiHost h, NVec at)
    {
        float d = Math.Max(0.1f, NVec.Distance(h.Head.Position, at));
        return 2f * d * MathF.Tan(ScreenAngle * 0.5f * MathF.PI / 180f) / Math.Max(1, Screen.height) * (size?.Value ?? 1f);
    }

    static NVec WorldToTracking(IVrUiHost h, NVec world)
    {
        var origin = h.TrackingToWorld(Pose.Identity);
        return NVec.Transform(world - origin.Position, NQuat.Inverse(origin.Rotation)) / Math.Max(1e-6f, h.WorldScale);
    }

    static NVec ToN(Vector3 v) => new(v.x, v.y, v.z);

    static Rect Union(List<Rect> rects)
    {
        var u = rects[0];
        foreach (var r in rects) u = Rect.MinMaxRect(Math.Min(u.xMin, r.xMin), Math.Min(u.yMin, r.yMin), Math.Max(u.xMax, r.xMax), Math.Max(u.yMax, r.yMax));
        return u;
    }
}
